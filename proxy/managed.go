// Managed mode: the allowlist reload loop, folded into the Go binary.
//
// The reload logic lives in-process so the managed proxy is a SINGLE self-contained
// static binary — no sidecar, no extra language runtime, nothing to supervise (see
// docs/production-hardening.md for the packaging rationale).
//
// Config source: a SINGLE JSON document in a locked-down Storage Account blob (see
// docs/allowlist.md for the contract):
//
//	{ "modules": [ {id, appid?, subnet?, allowed_hosts, action?}, ... ],
//	  "fallback": { "allowed_hosts": [...] } }     // optional; absent => deny-all default
//
// Flow:
//
//	Storage blob (egress-config/allowlist.json)
//	     |  this loop polls the blob's ETag (the change signal — no sentinel object
//	     |  needed, because a single blob is written atomically)
//	     v
//	render /render/acl.yaml  +  (netid) build the source-subnet role map from the SAME modules
//	     |
//	     v
//	(re)start smokescreen in-process  — the "reload = restart" cost, but with no pkill
//
// Triggered when ALLOWLIST_BLOB_URL or ALLOWLIST_BLOB_CONNECTION_STRING is set (see main.go).
// Single-document today; the schema (a modules array) is shaped so a future split to
// one blob per module is a localised change to blobClient/fetchAllowlist.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/azcore"
	"github.com/Azure/azure-sdk-for-go/sdk/azidentity"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob/blob"
	"github.com/sirupsen/logrus"
	"github.com/stripe/smokescreen/cmd"
	"github.com/stripe/smokescreen/pkg/smokescreen"
)

type module struct {
	ID string `json:"id"`
	// Appid is the workload's managed-identity CLIENT ID — the ACL key in the jwt /
	// basic-jwt identity modes (the proxy reads the same value from the validated token).
	Appid string `json:"appid"`
	// Subnet keys the module in the netid identity mode only.
	Subnet       string   `json:"subnet"`
	AllowedHosts []string `json:"allowed_hosts"`
	// Action is the Smokescreen ACL policy for this module: enforce | report | open.
	// Optional; an omitted or unrecognised value normalises to "enforce" (see
	// normalizeAction). "report" is the onboarding/discovery on-ramp — traffic passes but
	// off-list hosts are logged with enforce_would_deny:true so the allowlist can be tuned
	// before flipping to "enforce".
	Action string `json:"action"`
}

// fallback is the rule unidentified sources (no matching module) land on. It widens
// the default block from pure deny-all to a curated, platform-owned baseline allowlist —
// the pre-identity on-ramp (see docs/allowlist.md). The default block stays in ENFORCE
// mode regardless; fallback only contributes allowed_domains. Absent or empty =>
// deny-all (fail closed / secure by default).
type fallback struct {
	AllowedHosts []string `json:"allowed_hosts"`
}

// allowlistDoc is the single JSON document held in the blob.
type allowlistDoc struct {
	Modules  []module  `json:"modules"`
	Fallback *fallback `json:"fallback"`
}

// normalizeAction maps a config action value to a valid Smokescreen ACL action.
// Secure by default: anything other than the two explicit permissive modes (report/open)
// — including the empty string and typos — falls back to "enforce". report/open are never
// implicit; a module opts in on purpose, and the choice is visible in the rendered ACL.
func normalizeAction(id, raw string) string {
	switch strings.ToLower(strings.TrimSpace(raw)) {
	case "report":
		return "report"
	case "open":
		return "open"
	case "", "enforce":
		return "enforce"
	default:
		logrus.Warnf("module %s: unknown action %q, defaulting to enforce", id, raw)
		return "enforce"
	}
}

func envOr(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

// blobClient returns a client for the single allowlist blob. Preferred (Azure):
// ALLOWLIST_BLOB_URL (the full https blob URL) + a managed identity via
// DefaultAzureCredential — no secret on the host. On a VM/VMSS with a user-assigned
// identity, set AZURE_CLIENT_ID so the credential selects it. Fallback (local docker /
// Azurite): ALLOWLIST_BLOB_CONNECTION_STRING + ALLOWLIST_CONTAINER + ALLOWLIST_BLOB.
func blobClient() (*blob.Client, error) {
	if url := os.Getenv("ALLOWLIST_BLOB_URL"); url != "" {
		cred, err := azidentity.NewDefaultAzureCredential(nil)
		if err != nil {
			return nil, fmt.Errorf("managed identity: %w", err)
		}
		return blob.NewClient(url, cred, nil)
	}
	cs := os.Getenv("ALLOWLIST_BLOB_CONNECTION_STRING")
	svc, err := azblob.NewClientFromConnectionString(cs, nil)
	if err != nil {
		return nil, err
	}
	container := envOr("ALLOWLIST_CONTAINER", "egress-config")
	name := envOr("ALLOWLIST_BLOB", "allowlist.json")
	return svc.ServiceClient().NewContainerClient(container).NewBlobClient(name), nil
}

// fetchAllowlist downloads and parses the single JSON document, returning its ETag (the
// change signal the watcher polls).
func fetchAllowlist(ctx context.Context, c *blob.Client) (allowlistDoc, *azcore.ETag, error) {
	resp, err := c.DownloadStream(ctx, nil)
	if err != nil {
		return allowlistDoc{}, nil, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return allowlistDoc{}, nil, err
	}
	var doc allowlistDoc
	if err := json.Unmarshal(data, &doc); err != nil {
		return allowlistDoc{}, nil, fmt.Errorf("bad allowlist JSON: %w", err)
	}
	sort.Slice(doc.Modules, func(i, j int) bool { return doc.Modules[i].ID < doc.Modules[j].ID })
	return doc, resp.ETag, nil
}

// serviceName is the ACL key a module is rendered under — which must equal what the
// identity mode returns as the role. jwt/basic-jwt roles are the validated token's appid,
// so the ACL keys on the module's appid there; netid/basic-name roles are the module id.
func serviceName(mode string, m module) string {
	if mode == "jwt" || mode == "basic-jwt" {
		if m.Appid != "" {
			return m.Appid
		}
		// Without an appid this module can never match a token-derived role; render it
		// under its id (harmless) but flag the config gap.
		logrus.Warnf("module %s: no appid set; it cannot match any workload in %s mode", m.ID, mode)
	}
	return m.ID
}

// renderSmokescreenACL renders the per-service ACL. The default block governs unknown
// identities (no matching module): it stays ENFORCE and is seeded from the optional
// fallback allowlist — empty/absent fallback => deny-all (fail closed). report/open are
// never applied to the default block: a permissive default would let any unrecognised
// source egress, the opposite of secure-by-default.
func renderSmokescreenACL(mode string, mods []module, fb *fallback) string {
	var b strings.Builder
	b.WriteString("# generated from the egress allowlist blob — do not edit\n")
	b.WriteString("version: v1\nservices:\n")
	for _, m := range mods {
		fmt.Fprintf(&b, "  - name: %s\n    project: egress\n    action: %s\n    allowed_domains:\n", serviceName(mode, m), normalizeAction(m.ID, m.Action))
		for _, h := range m.AllowedHosts {
			fmt.Fprintf(&b, "      - %s\n", h)
		}
	}
	b.WriteString("default:\n  name: default\n  action: enforce\n  allowed_domains:")
	if fb == nil || len(fb.AllowedHosts) == 0 {
		b.WriteString(" []\n")
	} else {
		b.WriteString("\n")
		for _, h := range fb.AllowedHosts {
			fmt.Fprintf(&b, "    - %s\n", h)
		}
	}
	return b.String()
}

func writeFileAtomic(path, content string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, []byte(content), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path) // atomic
}

// cidrRolesFromModules builds the source-subnet -> role map from the SAME modules used for
// the ACL, so identity and allowlist can never drift (no separate SUBNET_ROLES in managed mode).
func cidrRolesFromModules(mods []module) []cidrRole {
	var out []cidrRole
	for _, m := range mods {
		if m.Subnet == "" {
			continue
		}
		_, n, err := net.ParseCIDR(m.Subnet)
		if err != nil {
			logrus.Warnf("module %s: bad subnet %q: %v", m.ID, m.Subnet, err)
			continue
		}
		out = append(out, cidrRole{n, m.ID})
	}
	return out
}

// managedRoleFromRequest selects the identity mechanism. The token modes are env-driven
// (token validation is independent of the allowlist source); netid derives its subnet map
// from the fetched modules.
func managedRoleFromRequest(mode string, mods []module) func(*http.Request) (string, error) {
	switch mode {
	case "jwt":
		return newJWTRole()
	case "basic-jwt":
		return newBasicJWTRole()
	case "basic-name":
		return newBasicNameRole()
	default:
		return netIDRoleFunc(cidrRolesFromModules(mods))
	}
}

// runManaged renders the ACL from the allowlist blob and supervises smokescreen, restarting
// it in-process whenever the blob's ETag changes. On startup with the blob unreachable it
// renders a deny-all ACL (fail closed) and keeps retrying.
func runManaged() {
	outputFile := envOr("OUTPUT_FILE", "/render/acl.yaml")
	poll := 10
	if v, err := strconv.Atoi(os.Getenv("POLL_SECONDS")); err == nil && v > 0 {
		poll = v
	}
	mode := os.Getenv("SMOKESCREEN_ID_MODE")

	for {
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		c, err := blobClient()
		var doc allowlistDoc
		var etag *azcore.ETag
		haveConfig := false
		if err == nil {
			if doc, etag, err = fetchAllowlist(ctx, c); err == nil {
				haveConfig = true
			}
		}
		cancel()
		if !haveConfig {
			logrus.Warnf("allowlist blob unreachable, rendering FAIL-CLOSED (deny-all): %v", err)
			doc = allowlistDoc{}
		} else {
			ids := make([]string, len(doc.Modules))
			for i, m := range doc.Modules {
				ids[i] = m.ID
			}
			et := ""
			if etag != nil {
				et = string(*etag)
			}
			logrus.Infof("rendered ACL from allowlist blob: modules=%v fallback=%t etag=%s", ids, doc.Fallback != nil, et)
		}

		if err := writeFileAtomic(outputFile, renderSmokescreenACL(mode, doc.Modules, doc.Fallback)); err != nil {
			logrus.Fatalf("write %s: %v", outputFile, err)
		}

		conf, err := cmd.NewConfiguration(nil, nil)
		if err != nil || conf == nil {
			logrus.Fatalf("Could not create configuration: %v", err)
		}
		conf.RoleFromRequest = managedRoleFromRequest(mode, doc.Modules)
		if isBasicMode(mode) {
			conf.RejectResponseHandlerWithCtx = basicChallengeHandler
		}
		applyJSONLogging(conf)

		// Watch the ETag; close quit (=> smokescreen shuts down, loop restarts it) when the
		// blob changes, or when the blob becomes reachable after a fail-closed start.
		quit := make(chan interface{})
		stop := make(chan struct{})
		go watchBlob(quit, stop, etag, haveConfig, time.Duration(poll)*time.Second)

		logrus.Infof("starting smokescreen (managed, mode=%s, poll=%ds)", mode, poll)
		smokescreen.StartWithConfig(conf, quit)
		close(stop) // smokescreen returned (blob change or exit); stop the watcher
		logrus.Info("smokescreen stopped; re-rendering and restarting")
	}
}

// watchBlob polls the blob's ETag and closes quit when the config should be reapplied —
// either the ETag changed, or the blob became reachable after a fail-closed start.
func watchBlob(quit chan interface{}, stop chan struct{}, last *azcore.ETag, haveConfig bool, every time.Duration) {
	t := time.NewTicker(every)
	defer t.Stop()
	for {
		select {
		case <-stop:
			return
		case <-t.C:
			ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
			c, err := blobClient()
			if err != nil {
				cancel()
				continue
			}
			props, err := c.GetProperties(ctx, nil)
			cancel()
			if err != nil {
				continue // hold last-known-good; retry next tick
			}
			if !haveConfig || last == nil || props.ETag == nil || *props.ETag != *last {
				close(quit)
				return
			}
		}
	}
}
