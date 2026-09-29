package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"reflect"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/azcore"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob/blob"
)

// fakeSource is a scripted allowlist blob: the test sets the current version and what a
// download of it returns, and reads back how often each version was downloaded.
type fakeSource struct {
	mu      sync.Mutex
	etag    azcore.ETag
	doc     allowlistDoc
	err     error // returned by Fetch; wrap errInvalidAllowlist for a malformed document
	down    bool  // ETag fails too, as when the blob is unreachable
	fetches map[azcore.ETag]int
}

func (f *fakeSource) set(etag string, doc allowlistDoc, err error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.etag, f.doc, f.err, f.down = azcore.ETag(etag), doc, err, false
}

func (f *fakeSource) fetchCount(etag string) int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.fetches[azcore.ETag(etag)]
}

func (f *fakeSource) ETag(context.Context) (*azcore.ETag, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.down {
		return nil, errors.New("blob unreachable")
	}
	e := f.etag
	return &e, nil
}

func (f *fakeSource) Fetch(context.Context) (allowlistDoc, *azcore.ETag, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.fetches == nil {
		f.fetches = map[azcore.ETag]int{}
	}
	f.fetches[f.etag]++
	e := f.etag
	if f.err != nil {
		if errors.Is(f.err, errInvalidAllowlist) {
			return allowlistDoc{}, &e, f.err
		}
		return allowlistDoc{}, nil, f.err
	}
	return f.doc, &e, nil
}

var malformed = fmt.Errorf("%w: bad JSON: unexpected end of JSON input", errInvalidAllowlist)

func docWith(id, host string) allowlistDoc {
	return allowlistDoc{Modules: []module{{ID: id, AllowedHosts: []string{host}}}}
}

// supervised runs superviseAllowlist against src and reports every document serve was
// started with; each start after the first is a smokescreen restart.
type supervised struct {
	served chan allowlistDoc
	cancel context.CancelFunc
	done   chan struct{}
}

func supervise(t *testing.T, src *fakeSource) *supervised {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	s := &supervised{served: make(chan allowlistDoc, 16), cancel: cancel, done: make(chan struct{})}
	go func() {
		defer close(s.done)
		superviseAllowlist(ctx, &allowlistWatch{src: src}, time.Millisecond,
			func(doc allowlistDoc, quit <-chan interface{}) {
				s.served <- doc
				<-quit
			})
	}()
	t.Cleanup(func() {
		cancel()
		<-s.done
	})
	return s
}

func (s *supervised) next(t *testing.T) allowlistDoc {
	t.Helper()
	select {
	case d := <-s.served:
		return d
	case <-time.After(2 * time.Second):
		t.Fatal("serve was not (re)started")
		return allowlistDoc{}
	}
}

func (s *supervised) noRestart(t *testing.T) {
	t.Helper()
	select {
	case d := <-s.served:
		t.Fatalf("serve restarted with %+v, want last-known-good kept running", d)
	case <-time.After(50 * time.Millisecond):
	}
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for !cond() {
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for %s", what)
		}
		time.Sleep(time.Millisecond)
	}
}

func sameACL(t *testing.T, got, want allowlistDoc) {
	t.Helper()
	g := renderSmokescreenACL("netid", got.Modules, got.Fallback)
	w := renderSmokescreenACL("netid", want.Modules, want.Fallback)
	if g != w {
		t.Errorf("rendered ACL:\n%s\nwant:\n%s", g, w)
	}
}

// A malformed push after a valid one keeps the valid allowlist in force without restarting
// smokescreen, is downloaded once rather than every poll, and the next valid push applies
// (issue #81).
func TestSuperviseKeepsLastKnownGoodOnInvalidDocument(t *testing.T) {
	a, b := docWith("mod-a", "a.example"), docWith("mod-b", "b.example")
	src := &fakeSource{}
	src.set("v1", a, nil)
	s := supervise(t, src)
	sameACL(t, s.next(t), a)

	src.set("v2", allowlistDoc{}, malformed)
	waitFor(t, "the malformed document to be fetched", func() bool { return src.fetchCount("v2") > 0 })
	s.noRestart(t)
	if n := src.fetchCount("v2"); n != 1 {
		t.Errorf("rejected document fetched %d times, want once until the blob changes", n)
	}

	src.set("v3", b, nil)
	sameACL(t, s.next(t), b)
}

// A failed download is transient: it keeps last-known-good without a restart and, unlike a
// malformed document, is retried on the same ETag.
func TestSuperviseRetriesFailedDownload(t *testing.T) {
	a, b := docWith("mod-a", "a.example"), docWith("mod-b", "b.example")
	src := &fakeSource{}
	src.set("v1", a, nil)
	s := supervise(t, src)
	sameACL(t, s.next(t), a)

	src.set("v2", allowlistDoc{}, errors.New("connection reset"))
	waitFor(t, "the download to be retried", func() bool { return src.fetchCount("v2") > 1 })
	s.noRestart(t)

	src.set("v2", b, nil)
	sameACL(t, s.next(t), b)
}

// With no valid document yet, the proxy still starts deny-all, and applies the first valid
// document once it appears.
func TestSuperviseMalformedOnFirstStartIsDenyAll(t *testing.T) {
	src := &fakeSource{}
	src.set("v1", allowlistDoc{}, malformed)
	s := supervise(t, src)
	first := s.next(t)
	if !reflect.DeepEqual(first, allowlistDoc{}) {
		t.Fatalf("first start served %+v, want the empty (deny-all) document", first)
	}
	sameACL(t, first, allowlistDoc{})
	s.noRestart(t)

	a := docWith("mod-a", "a.example")
	src.set("v2", a, nil)
	sameACL(t, s.next(t), a)
}

// An unreachable blob holds last-known-good, as before.
func TestSuperviseHoldsLastKnownGoodWhenBlobUnreachable(t *testing.T) {
	a := docWith("mod-a", "a.example")
	src := &fakeSource{}
	src.set("v1", a, nil)
	s := supervise(t, src)
	sameACL(t, s.next(t), a)

	src.mu.Lock()
	src.down = true
	src.mu.Unlock()
	s.noRestart(t)
}

// A document that parses is applied as it is: an empty allowlist is a legitimate deny-all
// push, not an error.
func TestSuperviseAppliesEmptyDocument(t *testing.T) {
	a := docWith("mod-a", "a.example")
	src := &fakeSource{}
	src.set("v1", a, nil)
	s := supervise(t, src)
	sameACL(t, s.next(t), a)

	src.set("v2", allowlistDoc{Modules: []module{}}, nil)
	sameACL(t, s.next(t), allowlistDoc{})
}

// fetchAllowlist separates a document that does not parse (errInvalidAllowlist, with the ETag
// of the rejected version) from one that does, including an empty one.
func TestFetchAllowlistClassifiesInvalidDocument(t *testing.T) {
	cases := []struct {
		name, body  string
		wantInvalid bool
	}{
		{"malformed", `{"modules": [`, true},
		{"wrong shape", `{"modules": "all"}`, true},
		{"empty object", `{}`, false},
		{"empty modules", `{"modules": []}`, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.Header().Set("ETag", `"0x8DE1"`)
				fmt.Fprint(w, tc.body)
			}))
			defer srv.Close()
			c, err := blob.NewClientWithNoCredential(srv.URL+"/egress-config/allowlist.json", nil)
			if err != nil {
				t.Fatal(err)
			}

			_, etag, err := fetchAllowlist(context.Background(), c)
			if got := errors.Is(err, errInvalidAllowlist); got != tc.wantInvalid {
				t.Fatalf("errors.Is(%v, errInvalidAllowlist) = %t, want %t", err, got, tc.wantInvalid)
			}
			if !tc.wantInvalid && err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if etag == nil || *etag != `"0x8DE1"` {
				t.Errorf("etag = %v, want the served ETag", etag)
			}
		})
	}
}

// captureConfigAudit redirects the CONFIG-REJECTED logger for one test and returns a func
// that decodes every row written so far, the way the DCR transform parses them.
func captureConfigAudit(t *testing.T) func() []map[string]any {
	t.Helper()
	buf := &bytes.Buffer{}
	configAuditLog.SetOutput(buf)
	t.Cleanup(func() { configAuditLog.SetOutput(os.Stderr) })
	return func() []map[string]any {
		var rows []map[string]any
		for _, line := range strings.Split(strings.TrimSpace(buf.String()), "\n") {
			if line == "" {
				continue
			}
			var row map[string]any
			if err := json.Unmarshal([]byte(line), &row); err != nil {
				t.Fatalf("audit line is not JSON, so the DCR could not parse it: %q: %v", line, err)
			}
			rows = append(rows, row)
		}
		return rows
	}
}

// pollN polls w n times and reports how many polls yielded a document to apply.
func pollN(w *allowlistWatch, n int) (applied int) {
	for range n {
		if _, ok := w.poll(context.Background()); ok {
			applied++
		}
	}
	return applied
}

// A rejected push is audited as one CANONICAL-PROXY-CONFIG-REJECTED row per rejected ETag,
// carrying both ETags and the decode error in fields the DCR already maps. It is not repeated
// while the same version stays in the blob; a valid push clears it, and a later bad push is
// audited again (issue #90).
func TestPollAuditsRejectedDocumentOncePerETag(t *testing.T) {
	rows := captureConfigAudit(t)
	src := &fakeSource{}
	w := &allowlistWatch{src: src}

	src.set("v1", docWith("mod-a", "a.example"), nil)
	if pollN(w, 3) != 1 {
		t.Fatal("valid document v1 was not applied exactly once")
	}
	if got := rows(); len(got) != 0 {
		t.Fatalf("valid document audited as rejected: %v", got)
	}

	src.set("v2", allowlistDoc{}, malformed)
	if pollN(w, 5) != 0 {
		t.Fatal("malformed document v2 was applied")
	}
	got := rows()
	if len(got) != 1 {
		t.Fatalf("got %d CONFIG-REJECTED rows over 5 polls of one bad version, want 1: %v", len(got), got)
	}
	assertConfigRejected(t, got[0], "allowlist etag=v2 rejected, keeping last-known-good etag=v1 until the blob changes")

	// A failed download is retried, not rejected, so it is not audited as a rejection.
	src.set("v3", allowlistDoc{}, errors.New("connection reset"))
	pollN(w, 3)
	if n := len(rows()); n != 1 {
		t.Fatalf("a failed download added CONFIG-REJECTED rows: %d, want still 1", n)
	}

	src.set("v4", docWith("mod-b", "b.example"), nil)
	if pollN(w, 3) != 1 {
		t.Fatal("valid document v4 was not applied exactly once")
	}
	if w.rejected != nil {
		t.Errorf("rejected = %q after a valid push, want it cleared", etagString(w.rejected))
	}

	src.set("v5", allowlistDoc{}, malformed)
	pollN(w, 3)
	got = rows()
	if len(got) != 2 {
		t.Fatalf("got %d CONFIG-REJECTED rows, want a second one for the new bad version v5: %v", len(got), got)
	}
	assertConfigRejected(t, got[1], "allowlist etag=v5 rejected, keeping last-known-good etag=v4 until the blob changes")
}

// Rejected at startup there is no last-known-good, so the row says the proxy stays deny-all.
// It is JSON even though the standard logger is still logrus text until smokescreen starts.
func TestPollAuditsRejectedDocumentOnFirstStart(t *testing.T) {
	rows := captureConfigAudit(t)
	src := &fakeSource{}
	src.set("v1", allowlistDoc{}, malformed)
	pollN(&allowlistWatch{src: src}, 3)

	got := rows()
	if len(got) != 1 {
		t.Fatalf("got %d CONFIG-REJECTED rows, want 1: %v", len(got), got)
	}
	assertConfigRejected(t, got[0], "allowlist etag=v1 rejected, staying FAIL-CLOSED (deny-all) until the blob changes")
}

// assertConfigRejected checks a row the way the DCR transform reads it: routed on the
// CANONICAL-PROXY prefix of msg, DecisionReason and Error taken from their mapped fields.
func assertConfigRejected(t *testing.T, row map[string]any, wantReason string) {
	t.Helper()
	if msg, _ := row["msg"].(string); msg != canonicalProxyConfigRejected || !strings.HasPrefix(msg, "CANONICAL-PROXY-") {
		t.Errorf("msg = %v, want %s", row["msg"], canonicalProxyConfigRejected)
	}
	if row["level"] != "warning" {
		t.Errorf("level = %v, want warning", row["level"])
	}
	if row["decision_reason"] != wantReason {
		t.Errorf("decision_reason = %q\nwant              %q", row["decision_reason"], wantReason)
	}
	if row["error"] != malformed.Error() {
		t.Errorf("error = %q, want the decode error %q", row["error"], malformed.Error())
	}
}
