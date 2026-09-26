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
	"errors"
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

// errInvalidAllowlist marks a document that downloaded but does not parse. Unlike a failed
// download it will not fix itself, so the watcher remembers its ETag rather than retrying it.
var errInvalidAllowlist = errors.New("invalid allowlist document")

// fetchAllowlist downloads and parses the single JSON document, returning its ETag (the
// change signal the watcher polls). A parse failure wraps errInvalidAllowlist and still
// returns the ETag, so the rejected version can be named and skipped.
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
		return allowlistDoc{}, resp.ETag, fmt.Errorf("%w: bad JSON: %w", errInvalidAllowlist, err)
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
	// An empty `services:` decodes to nil, which smokescreen rejects as a missing list and
	// exits; `services: []` is the empty list, so a module-less (deny-all) ACL still loads.
	if len(mods) == 0 {
		b.WriteString("version: v1\nservices: []\n")
	} else {
		b.WriteString("version: v1\nservices:\n")
	}
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

// newManagedRoleFunc selects the identity mechanism and returns the per-reload builder that
// runManaged calls with each fetched module set. The token modes are env-driven (token
// validation is independent of the allowlist source), so their role func, and with it the
// JWKS client, is built once here and reused across reloads: rebuilding it per reload would
// re-fetch the JWKS each time and leave the old client's refresh goroutine running. Only
// netid derives anything from the modules: its subnet map.
func newManagedRoleFunc(mode string) func(mods []module) func(*http.Request) (string, error) {
	var fixed func(*http.Request) (string, error)
	switch mode {
	case "jwt":
		fixed = withRoleErrorDetail(newJWTRole())
	case "basic-jwt":
		fixed = withRoleErrorDetail(newBasicJWTRole())
	case "basic-name":
		fixed = withRoleErrorDetail(newBasicNameRole())
	default:
		return func(mods []module) func(*http.Request) (string, error) {
			return withRoleErrorDetail(netIDRoleFunc(cidrRolesFromModules(mods)))
		}
	}
	return func([]module) func(*http.Request) (string, error) { return fixed }
}

// allowlistSource is where the allowlist document comes from: the blob, or a fake in tests.
type allowlistSource interface {
	// ETag returns the current version of the document without downloading it.
	ETag(ctx context.Context) (*azcore.ETag, error)
	// Fetch downloads and parses the document (see fetchAllowlist).
	Fetch(ctx context.Context) (allowlistDoc, *azcore.ETag, error)
}

type blobSource struct{}

func (blobSource) ETag(ctx context.Context) (*azcore.ETag, error) {
	c, err := blobClient()
	if err != nil {
		return nil, err
	}
	props, err := c.GetProperties(ctx, nil)
	if err != nil {
		return nil, err
	}
	return props.ETag, nil
}

func (blobSource) Fetch(ctx context.Context) (allowlistDoc, *azcore.ETag, error) {
	c, err := blobClient()
	if err != nil {
		return allowlistDoc{}, nil, err
	}
	return fetchAllowlist(ctx, c)
}

// allowlistWatch decides, one poll at a time, whether there is a new valid document to apply.
// It remembers the ETag in force and the ETag of the last document that failed to parse, so
// a bad push is logged once and skipped until the blob changes again.
type allowlistWatch struct {
	src      allowlistSource
	applied  *azcore.ETag // ETag of the document in force; nil while serving fail-closed deny-all
	rejected *azcore.ETag // ETag of the last document that did not parse
}

// poll checks the blob once and returns a document to apply, or ok=false to keep serving the
// current one. Nothing here ever replaces a valid document with deny-all: an unreachable
// blob, a failed download, or an unparseable document all keep last-known-good in force.
func (w *allowlistWatch) poll(ctx context.Context) (doc allowlistDoc, ok bool) {
	etag, err := w.src.ETag(ctx)
	if err != nil {
		if w.applied == nil {
			logrus.Warnf("allowlist blob unreachable, %s: %v", w.holding(), err)
		}
		return allowlistDoc{}, false // hold last-known-good; retry next tick
	}
	if sameETag(etag, w.applied) || sameETag(etag, w.rejected) {
		return allowlistDoc{}, false
	}

	doc, fetched, err := w.src.Fetch(ctx)
	switch {
	case errors.Is(err, errInvalidAllowlist):
		if fetched == nil {
			fetched = etag
		}
		w.rejected = fetched
		logrus.Warnf("allowlist blob etag=%s rejected, %s until the blob changes: %v",
			etagString(fetched), w.holding(), err)
		return allowlistDoc{}, false
	case err != nil:
		logrus.Warnf("allowlist blob etag=%s could not be downloaded, %s; retrying: %v",
			etagString(etag), w.holding(), err)
		return allowlistDoc{}, false
	}

	w.applied, w.rejected = fetched, nil
	ids := make([]string, len(doc.Modules))
	for i, m := range doc.Modules {
		ids[i] = m.ID
	}
	logrus.Infof("loaded allowlist blob: modules=%v fallback=%t etag=%s", ids, doc.Fallback != nil, etagString(fetched))
	return doc, true
}

// holding describes what stays in force while a new document cannot be applied.
func (w *allowlistWatch) holding() string {
	if w.applied == nil {
		return "staying FAIL-CLOSED (deny-all)"
	}
	return "keeping last-known-good etag=" + etagString(w.applied)
}

func sameETag(a, b *azcore.ETag) bool { return a != nil && b != nil && *a == *b }

func etagString(e *azcore.ETag) string {
	if e == nil {
		return ""
	}
	return string(*e)
}

// runManaged renders the ACL from the allowlist blob and supervises smokescreen, restarting
// it in-process whenever a new valid document is published. On startup with no valid
// document it renders a deny-all ACL (fail closed) and keeps retrying.
func runManaged() {
	outputFile := envOr("OUTPUT_FILE", "/render/acl.yaml")
	poll := 10
	if v, err := strconv.Atoi(os.Getenv("POLL_SECONDS")); err == nil && v > 0 {
		poll = v
	}
	mode := os.Getenv("SMOKESCREEN_ID_MODE")
	roleFor := newManagedRoleFunc(mode)

	w := &allowlistWatch{src: blobSource{}}
	superviseAllowlist(context.Background(), w, time.Duration(poll)*time.Second,
		func(doc allowlistDoc, quit <-chan interface{}) {
			if err := writeFileAtomic(outputFile, renderSmokescreenACL(mode, doc.Modules, doc.Fallback)); err != nil {
				logrus.Fatalf("write %s: %v", outputFile, err)
			}
			conf, err := cmd.NewConfiguration(nil, nil)
			if err != nil || conf == nil {
				logrus.Fatalf("could not create configuration: %v", err)
			}
			conf.RoleFromRequest = roleFor(doc.Modules)
			conf.RejectResponseHandlerWithCtx = newRejectHandler(mode)
			applyJSONLogging(conf)

			logrus.Infof("starting smokescreen (managed, mode=%s, poll=%ds)", mode, poll)
			smokescreen.StartWithConfig(conf, quit)
			logrus.Info("smokescreen stopped")
		})
}

// allowlistPollTimeout bounds one poll: the ETag check plus, on a change, the download.
const allowlistPollTimeout = 15 * time.Second

// superviseAllowlist serves the allowlist (serve renders it and runs smokescreen until quit
// closes) and restarts serve only when the watcher yields a new valid document, so a bad
// push never drops open tunnels. It starts deny-all when no valid document can be loaded,
// and returns when ctx ends.
func superviseAllowlist(ctx context.Context, w *allowlistWatch, every time.Duration,
	serve func(doc allowlistDoc, quit <-chan interface{})) {
	pctx, cancel := context.WithTimeout(ctx, allowlistPollTimeout)
	doc, _ := w.poll(pctx) // no valid document: the zero doc renders deny-all
	cancel()

	for {
		quit := make(chan interface{})
		next := make(chan allowlistDoc, 1)
		stop := make(chan struct{})
		done := make(chan struct{})
		go func() {
			defer close(done)
			t := time.NewTicker(every)
			defer t.Stop()
			for {
				select {
				case <-stop:
					return
				case <-ctx.Done():
					close(quit)
					return
				case <-t.C:
					pctx, cancel := context.WithTimeout(ctx, allowlistPollTimeout)
					d, ok := w.poll(pctx)
					cancel()
					if ok {
						next <- d
						close(quit)
						return
					}
				}
			}
		}()

		serve(doc, quit)
		close(stop)
		<-done // the watcher owns w while it runs; wait before the next one starts
		if ctx.Err() != nil {
			return
		}
		select {
		case doc = <-next:
			logrus.Info("re-rendering and restarting smokescreen with the new allowlist")
		default:
			// serve returned on its own; restart it on the same document
		}
	}
}
