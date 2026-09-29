package main

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"sync/atomic"
	"syscall"
	"testing"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/azcore"
	"github.com/sirupsen/logrus"
	logtest "github.com/sirupsen/logrus/hooks/test"
)

func quietHealth() *health {
	l := logrus.New()
	l.SetOutput(io.Discard)
	return newHealth(l)
}

// readyUpToAllowlist is a health state past the configuration and key steps, waiting for the
// first allowlist: where the reload loop starts.
func readyUpToAllowlist() *health {
	h := quietHealth()
	h.configValid()
	h.keysLoaded()
	return h
}

func get(t *testing.T, h http.Handler, method, path string) (int, string) {
	t.Helper()
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(method, path, nil))
	return rec.Code, rec.Body.String()
}

func wantReadyz(t *testing.T, h *health, code int, status, reason string) {
	t.Helper()
	gotCode, body := get(t, h.handler(), http.MethodGet, "/readyz")
	var b healthBody
	if err := json.Unmarshal([]byte(body), &b); err != nil {
		t.Fatalf("/readyz body %q: %v", body, err)
	}
	if gotCode != code || b.Status != status || b.Reason != reason {
		t.Fatalf("/readyz = %d %s/%s, want %d %s/%s", gotCode, b.Status, b.Reason, code, status, reason)
	}
	wantState := ""
	if code == http.StatusOK {
		wantState = "Healthy"
	}
	if b.ApplicationHealthState != wantState {
		t.Fatalf("/readyz ApplicationHealthState = %q, want %q (never Unhealthy)", b.ApplicationHealthState, wantState)
	}
}

func wantLivez(t *testing.T, h *health, code int) {
	t.Helper()
	if got, body := get(t, h.handler(), http.MethodGet, "/livez"); got != code {
		t.Fatalf("/livez = %d %s, want %d", got, body, code)
	}
}

// Each lifecycle row of docs/health.md, in the order the proxy moves through them.
func TestReadyzLifecycle(t *testing.T) {
	h := quietHealth()
	wantReadyz(t, h, 503, "not-ready", "config")
	h.configValid()
	wantReadyz(t, h, 503, "not-ready", "keys")
	h.keysLoaded()
	wantReadyz(t, h, 503, "not-ready", "allowlist")
	h.allowlistState(false, true) // a malformed first document: still nothing to serve
	wantReadyz(t, h, 503, "not-ready", "allowlist")
	h.allowlistState(true, false)
	wantReadyz(t, h, 200, "ok", "ok")
	h.allowlistState(true, true) // invalid push, last-known-good in force
	wantReadyz(t, h, 200, "degraded", "rejected")
	h.allowlistState(true, false) // a valid push applied
	wantReadyz(t, h, 200, "ok", "ok")
	h.startShutdown()
	wantReadyz(t, h, 503, "not-ready", "shutdown")
	h.allowlistState(true, false) // a late poll does not undo shutdown
	wantReadyz(t, h, 503, "not-ready", "shutdown")
}

// The 200 body is exactly what the Application Health extension's rich states read, and no
// body carries configuration: no URL, ID or ETag.
func TestReadyzBody(t *testing.T) {
	h := readyUpToAllowlist()
	if code, body := get(t, h.handler(), http.MethodGet, "/readyz"); code != 503 ||
		strings.TrimSpace(body) != `{"status":"not-ready","reason":"allowlist"}` {
		t.Errorf("not ready: %d %s", code, body)
	}
	h.allowlistState(true, false)
	code, body := get(t, h.handler(), http.MethodGet, "/readyz")
	if code != 200 || strings.TrimSpace(body) != `{"ApplicationHealthState":"Healthy","status":"ok","reason":"ok"}` {
		t.Errorf("ready: %d %s", code, body)
	}
	rec := httptest.NewRecorder()
	h.handler().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/readyz", nil))
	if ct := rec.Header().Get("Content-Type"); ct != "application/json" {
		t.Errorf("Content-Type = %q", ct)
	}
}

// Only /readyz and /livez exist, for GET (and HEAD); anything else is not a health answer.
func TestHealthRoutes(t *testing.T) {
	h := readyUpToAllowlist()
	for _, tc := range []struct {
		method, path string
		code         int
	}{
		{http.MethodHead, "/readyz", 503},
		{http.MethodHead, "/livez", 200},
		{http.MethodPost, "/readyz", 405},
		{http.MethodGet, "/healthcheck", 404},
		{http.MethodGet, "/", 404},
	} {
		if code, _ := get(t, h.handler(), tc.method, tc.path); code != tc.code {
			t.Errorf("%s %s = %d, want %d", tc.method, tc.path, code, tc.code)
		}
	}
}

// Each change of verdict is logged once with its reason; repeated state and probes are not.
func TestReadinessTransitionsLoggedOnce(t *testing.T) {
	l, hook := logtest.NewNullLogger()
	h := newHealth(l)
	h.configValid()
	h.keysLoaded()
	for range 3 {
		h.allowlistState(false, false) // repeated failed polls
	}
	h.allowlistState(true, false)
	h.allowlistState(true, false)
	for range 5 {
		get(t, h.handler(), http.MethodGet, "/readyz")
	}
	h.allowlistState(true, true)
	h.startShutdown()

	var got []string
	for _, e := range hook.AllEntries() {
		got = append(got, e.Data["status"].(string)+"/"+e.Data["reason"].(string))
	}
	want := []string{"not-ready/keys", "not-ready/allowlist", "ok/ok", "degraded/rejected", "not-ready/shutdown"}
	if strings.Join(got, " ") != strings.Join(want, " ") {
		t.Errorf("transitions logged: %v, want %v", got, want)
	}
}

// Port 4750 follows readiness: nothing listens on the proxy port until the first allowlist
// is in force, and /readyz says 503 (allowlist) until then.
func TestProxyPortClosedUntilReady(t *testing.T) {
	probe, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	proxyAddr := probe.Addr().String()
	probe.Close()

	src := &fakeSource{down: true}
	h := readyUpToAllowlist()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	serving := make(chan struct{}, 1)
	go func() {
		defer close(done)
		superviseAllowlist(ctx, &allowlistWatch{src: src}, time.Millisecond, h,
			func(_ allowlistDoc, quit <-chan interface{}) {
				ln, err := net.Listen("tcp", proxyAddr)
				if err != nil {
					t.Error(err)
					return
				}
				defer ln.Close()
				serving <- struct{}{}
				<-quit
			})
	}()
	t.Cleanup(func() { cancel(); <-done })

	time.Sleep(50 * time.Millisecond) // many polls against the unreachable blob
	if c, err := net.DialTimeout("tcp", proxyAddr, time.Second); err == nil {
		c.Close()
		t.Fatal("proxy port accepted a connection before the first allowlist")
	}
	wantReadyz(t, h, 503, "not-ready", "allowlist")

	src.set("v1", docWith("mod-a", "a.example"), nil)
	select {
	case <-serving:
	case <-time.After(2 * time.Second):
		t.Fatal("proxy did not start once the allowlist arrived")
	}
	wantReadyz(t, h, 200, "ok", "ok")
	c, err := net.DialTimeout("tcp", proxyAddr, time.Second)
	if err != nil {
		t.Fatalf("proxy port closed after ready: %v", err)
	}
	c.Close()
}

// A blob outage after ready keeps last-known-good in force, and the instance ready.
func TestReadyzLastKnownGoodAfterBlobOutage(t *testing.T) {
	src := &fakeSource{}
	src.set("v1", docWith("mod-a", "a.example"), nil)
	s := supervise(t, src)
	s.next(t)
	src.mu.Lock()
	src.down = true
	src.mu.Unlock()
	s.noRestart(t)
	wantReadyz(t, s.health, 200, "ok", "ok")
	wantLivez(t, s.health, 200)
}

// An invalid push after ready is degraded (still 200) until a valid document is applied.
func TestReadyzDegradedAfterInvalidPush(t *testing.T) {
	src := &fakeSource{}
	src.set("v1", docWith("mod-a", "a.example"), nil)
	s := supervise(t, src)
	s.next(t)

	src.set("v2", allowlistDoc{}, malformed)
	waitFor(t, "the malformed document to be fetched", func() bool { return src.fetchCount("v2") > 0 })
	s.noRestart(t)
	wantReadyz(t, s.health, 200, "degraded", "rejected")

	src.set("v3", docWith("mod-b", "b.example"), nil)
	s.next(t)
	wantReadyz(t, s.health, 200, "ok", "ok")
}

// On shutdown smokescreen drains and returns by itself; the supervisor must then stop rather
// than restart it, and /readyz stays 503 (shutdown) throughout.
func TestSuperviseStopsAfterShutdown(t *testing.T) {
	src := &fakeSource{}
	src.set("v1", docWith("mod-a", "a.example"), nil)
	h := readyUpToAllowlist()
	drained := make(chan struct{})
	starts := make(chan struct{}, 4)
	done := make(chan struct{})
	go func() {
		defer close(done)
		superviseAllowlist(context.Background(), &allowlistWatch{src: src}, time.Millisecond, h,
			func(_ allowlistDoc, _ <-chan interface{}) {
				starts <- struct{}{}
				<-drained // smokescreen's own SIGTERM handling: drain, then return
			})
	}()
	<-starts
	wantReadyz(t, h, 200, "ok", "ok")

	if serving := h.startShutdown(); !serving {
		t.Error("startShutdown: serving = false while smokescreen runs")
	}
	wantReadyz(t, h, 503, "not-ready", "shutdown")
	wantLivez(t, h, 200)
	close(drained)
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("supervisor did not return after the drained smokescreen stopped")
	}
	if len(starts) != 0 {
		t.Error("smokescreen was restarted during shutdown")
	}
}

// Shutdown before the first allowlist returns straight away: there is nothing to drain.
func TestSuperviseStopsWhileWaitingForAllowlist(t *testing.T) {
	h := readyUpToAllowlist()
	done := make(chan struct{})
	go func() {
		defer close(done)
		superviseAllowlist(context.Background(), &allowlistWatch{src: &fakeSource{down: true}}, time.Millisecond, h,
			func(allowlistDoc, <-chan interface{}) { t.Error("served without an allowlist") })
	}()
	time.Sleep(10 * time.Millisecond)
	h.startShutdown()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("supervisor kept waiting for an allowlist after shutdown started")
	}
}

// SIGTERM flips /readyz at once. With smokescreen serving, it drains by itself (and gets a
// backstop signal); with nothing serving, the process exits now.
func TestHandleShutdown(t *testing.T) {
	for _, serving := range []bool{false, true} {
		h := readyUpToAllowlist()
		h.allowlistState(true, false)
		if serving && !h.beginServing() {
			t.Fatal("beginServing refused before shutdown")
		}
		sigs := make(chan os.Signal, 1)
		exited, reraised := make(chan int, 1), make(chan struct{}, 1)
		done := make(chan struct{})
		go func() {
			defer close(done)
			handleShutdown(h, sigs, func(c int) { exited <- c }, func() { reraised <- struct{}{} })
		}()
		wantReadyz(t, h, 200, "ok", "ok")
		sigs <- syscall.SIGTERM
		<-done
		wantReadyz(t, h, 503, "not-ready", "shutdown")
		switch {
		case serving && len(exited) != 0:
			t.Error("exited while smokescreen was draining")
		case serving && len(reraised) != 1:
			t.Error("no backstop SIGTERM while serving")
		case !serving && len(reraised) != 0:
			t.Error("re-raised SIGTERM with nothing serving")
		case !serving && (len(exited) != 1 || <-exited != 0):
			t.Error("did not exit 0 with nothing serving")
		}
		if h.beginServing() {
			t.Error("beginServing allowed a (re)start after shutdown started")
		}
	}
}

// /livez never depends on the blob or the JWKS: 200 while the keys cannot be loaded, and 200
// through an unreachable blob, because a failed poll still finishes.
func TestLivezThroughBlobAndJWKSOutage(t *testing.T) {
	unreachable := httptest.NewServer(http.NotFoundHandler())
	jwksURL := unreachable.URL + "/keys"
	unreachable.Close()

	// The listener runs before and during key loading, as in startHealth.
	h := quietHealth()
	h.configValid()
	addr, err := serveHealth(h, "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	loaded := make(chan error, 1)
	go func() {
		_, _, err := loadJWKS(jwksURL, 3, 20*time.Millisecond)
		loaded <- err
	}()
	for _, tc := range []struct {
		path string
		code int
	}{{"/readyz", 503}, {"/livez", 200}} {
		resp, err := http.Get("http://" + addr.String() + tc.path)
		if err != nil {
			t.Fatalf("health listener not answering during key load: %v", err)
		}
		resp.Body.Close()
		if resp.StatusCode != tc.code {
			t.Errorf("%s during JWKS outage = %d, want %d", tc.path, resp.StatusCode, tc.code)
		}
	}
	if err := <-loaded; err == nil {
		t.Fatal("loadJWKS succeeded against an unreachable URL")
	}
	wantReadyz(t, h, 503, "not-ready", "keys")

	h.keysLoaded()
	src := &fakeSource{}
	src.set("v1", docWith("mod-a", "a.example"), nil)
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		superviseAllowlist(ctx, &allowlistWatch{src: src}, time.Millisecond, h,
			func(_ allowlistDoc, quit <-chan interface{}) { <-quit })
	}()
	t.Cleanup(func() { cancel(); <-done })
	waitFor(t, "ready", func() bool { return h.readiness().ready })
	src.mu.Lock()
	src.down = true
	src.mu.Unlock()
	for range 20 {
		wantLivez(t, h, 200)
		time.Sleep(2 * time.Millisecond)
	}
}

// hangingSource answers the first ETag check with a valid document, then hangs every later one
// until its context ends: a blob that stops answering rather than refusing.
type hangingSource struct{ calls atomic.Int32 }

func (s *hangingSource) ETag(ctx context.Context) (*azcore.ETag, error) {
	if s.calls.Add(1) > 1 {
		<-ctx.Done()
		return nil, ctx.Err()
	}
	e := azcore.ETag("v1")
	return &e, nil
}

func (s *hangingSource) Fetch(context.Context) (allowlistDoc, *azcore.ETag, error) {
	e := azcore.ETag("v1")
	return docWith("mod-a", "a.example"), &e, nil
}

// A shutdown while a poll hangs on the blob must not wait out the poll timeout: once
// smokescreen has drained, the supervisor returns and the process exits.
func TestShutdownDoesNotWaitForHangingPoll(t *testing.T) {
	src := &hangingSource{}
	h := readyUpToAllowlist()
	drained := make(chan struct{})
	serving := make(chan struct{}, 1)
	done := make(chan struct{})
	go func() {
		defer close(done)
		superviseAllowlist(context.Background(), &allowlistWatch{src: src}, time.Millisecond, h,
			func(_ allowlistDoc, _ <-chan interface{}) {
				serving <- struct{}{}
				<-drained
			})
	}()
	<-serving
	waitFor(t, "a poll to hang", func() bool { return src.calls.Load() > 1 })
	h.startShutdown()
	close(drained)
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatalf("supervisor still waiting on a hanging poll after shutdown (poll timeout %s)", allowlistPollTimeout)
	}
}

// /livez turns 503 only when the reload loop stops finishing polls, and recovers when it does.
func TestLivezDetectsStalledLoop(t *testing.T) {
	now := time.Unix(0, 0)
	h := readyUpToAllowlist()
	h.now = func() time.Time { return now }
	wantLivez(t, h, 200) // before the loop starts: nothing to be stuck

	h.watchLoop(10 * time.Second)
	if h.stallAfter != time.Minute {
		t.Errorf("stallAfter = %s, want 1m for a 10s poll", h.stallAfter)
	}
	now = now.Add(time.Minute)
	wantLivez(t, h, 200)
	now = now.Add(time.Second)
	wantLivez(t, h, 503)
	h.beat()
	wantLivez(t, h, 200)

	now = now.Add(time.Hour)
	h.startShutdown()
	wantLivez(t, h, 200) // draining is not stuck
}
