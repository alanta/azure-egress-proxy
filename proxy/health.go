// Health listener: /readyz and /livez on their own port (HEALTH_ADDR). See docs/health.md.
//
// The listener starts once, before the signing keys load, and lives outside smokescreen: every
// allowlist reload closes smokescreen's listener, and the health answer must survive that. It
// never shares the proxy port, so a health request can never be read as a proxy request, and
// it never reaches smokescreen's decision log.
//
// Readiness is what the proxy port follows: the proxy opens 4750 only once /readyz would say
// 200, so anything that probes TCP 4750 (the load balancer, today's scale-set health extension)
// gets the same answer.
package main

import (
	"encoding/json"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/sirupsen/logrus"
)

const (
	healthAddrEnv     = "HEALTH_ADDR"
	defaultHealthAddr = "127.0.0.1:4751"

	// shutdownReraiseDelay is how long after SIGTERM the proxy sends itself a second one,
	// in case smokescreen was between starting and listening for signals (see handleShutdown).
	shutdownReraiseDelay = time.Second
)

// readiness is one /readyz answer. It is compared as a value to log each change once.
type readiness struct {
	ready  bool
	status string // ok | degraded | not-ready
	reason string // ok | rejected | config | keys | allowlist | shutdown
}

// health holds what readiness and liveness are computed from. Each input is set by the part
// of the proxy that owns it; the HTTP handlers only read.
type health struct {
	mu  sync.Mutex
	log logrus.FieldLogger
	now func() time.Time

	configOK     bool // the startup configuration check passed
	keysOK       bool // signing keys loaded, or the identity mode needs none
	allowlistOK  bool // an allowlist has been in force at least once since start
	rejected     bool // the latest pushed allowlist did not parse; last-known-good is in force
	shuttingDown bool
	serving      bool // smokescreen is running (between beginServing and endServing)
	stopping     chan struct{}
	last         readiness

	// Liveness: the reload loop beats on every poll. Before it starts (standalone mode, or
	// while the keys load) there is nothing that can get stuck, so /livez answers 200.
	loopStarted bool
	lastBeat    time.Time
	stallAfter  time.Duration
	stalled     bool
}

func newHealth(log logrus.FieldLogger) *health {
	return &health{
		log:      log,
		now:      time.Now,
		stopping: make(chan struct{}),
		last:     readiness{status: "not-ready", reason: "config"},
	}
}

// readinessLocked derives the verdict. Shutdown wins over everything; then the startup steps
// in the order they happen; a rejected push is still ready (degraded), on purpose.
func (h *health) readinessLocked() readiness {
	switch {
	case h.shuttingDown:
		return readiness{status: "not-ready", reason: "shutdown"}
	case !h.configOK:
		return readiness{status: "not-ready", reason: "config"}
	case !h.keysOK:
		return readiness{status: "not-ready", reason: "keys"}
	case !h.allowlistOK:
		return readiness{status: "not-ready", reason: "allowlist"}
	case h.rejected:
		return readiness{ready: true, status: "degraded", reason: "rejected"}
	default:
		return readiness{ready: true, status: "ok", reason: "ok"}
	}
}

// update applies a change and logs the readiness transition, once per change. Probes never
// log; only a change of verdict does.
func (h *health) update(change func()) {
	h.mu.Lock()
	defer h.mu.Unlock()
	change()
	r := h.readinessLocked()
	if r == h.last {
		return
	}
	h.last = r
	h.log.WithFields(logrus.Fields{
		"ready": r.ready, "status": r.status, "reason": r.reason,
	}).Infof("readiness changed: %s (%s)", r.status, r.reason)
}

func (h *health) readiness() readiness {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.readinessLocked()
}

func (h *health) configValid() { h.update(func() { h.configOK = true }) }
func (h *health) keysLoaded()  { h.update(func() { h.keysOK = true }) }

// allowlistState records the watcher's state after a poll: applied once any valid document is
// in force, rejected while the latest pushed document is the one that did not parse.
func (h *health) allowlistState(applied, rejected bool) {
	h.update(func() { h.allowlistOK, h.rejected = applied, rejected })
}

// beginServing reports whether smokescreen may start (and the proxy port open). It is false
// once shutdown has started, so a shutdown can never race a (re)start.
func (h *health) beginServing() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.shuttingDown {
		return false
	}
	h.serving = true
	return true
}

func (h *health) endServing() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.serving = false
}

// startShutdown flips /readyz to 503 (shutdown) and reports whether smokescreen was serving.
func (h *health) startShutdown() (serving bool) {
	h.update(func() {
		if !h.shuttingDown {
			h.shuttingDown = true
			close(h.stopping)
		}
		serving = h.serving
	})
	return serving
}

func (h *health) isShuttingDown() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.shuttingDown
}

// watchLoop marks the start of the reload loop polling every `every`. The loop counts as stuck
// once no poll has finished for three intervals plus a poll's own timeout; a reload's restart
// gap fits inside that, and it is never less than a minute.
func (h *health) watchLoop(every time.Duration) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.stallAfter = max(3*every+allowlistPollTimeout, time.Minute)
	h.loopStarted = true
	h.lastBeat = h.now()
}

// beat records that the reload loop made progress: a poll finished, whatever its result.
func (h *health) beat() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.lastBeat = h.now()
}

// live reports whether the process is working. It never depends on the blob or the JWKS: a
// poll that fails still finishes, and a finished poll is progress.
func (h *health) live() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if !h.loopStarted || h.shuttingDown {
		return true
	}
	stalled := h.now().Sub(h.lastBeat) > h.stallAfter
	if stalled != h.stalled {
		h.stalled = stalled
		if stalled {
			h.log.Errorf("liveness: the allowlist reload loop has not finished a poll for more than %s", h.stallAfter)
		} else {
			h.log.Info("liveness: the allowlist reload loop is making progress again")
		}
	}
	return !stalled
}

// healthBody is the /readyz and /livez response. ApplicationHealthState is the field the scale
// set's Application Health extension reads (rich health states); it is set only on a 200, and
// the proxy never reports "Unhealthy", which would ask Azure to repair the instance. The body
// never carries URLs, IDs, ETags or allowlist contents.
type healthBody struct {
	ApplicationHealthState string `json:"ApplicationHealthState,omitempty"`
	Status                 string `json:"status"`
	Reason                 string `json:"reason,omitempty"`
}

func (h *health) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /readyz", func(w http.ResponseWriter, _ *http.Request) {
		r := h.readiness()
		if r.ready {
			writeHealth(w, http.StatusOK, healthBody{"Healthy", r.status, r.reason})
			return
		}
		writeHealth(w, http.StatusServiceUnavailable, healthBody{"", r.status, r.reason})
	})
	mux.HandleFunc("GET /livez", func(w http.ResponseWriter, _ *http.Request) {
		if h.live() {
			writeHealth(w, http.StatusOK, healthBody{Status: "ok"})
			return
		}
		writeHealth(w, http.StatusServiceUnavailable, healthBody{Status: "stalled"})
	})
	return mux
}

func writeHealth(w http.ResponseWriter, code int, body healthBody) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(body)
}

// serveHealth binds addr and serves the health endpoints for the life of the process.
func serveHealth(h *health, addr string) (net.Addr, error) {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, err
	}
	srv := &http.Server{
		Handler:           h.handler(),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       5 * time.Second,
		WriteTimeout:      5 * time.Second,
	}
	go func() {
		if err := srv.Serve(ln); err != nil {
			logrus.Fatalf("health listener on %s stopped: %v", addr, err)
		}
	}()
	return ln.Addr(), nil
}

// startHealth runs once the configuration check has passed and before the signing keys load:
// it starts the health listener (503 keys from here on) and takes over SIGTERM so /readyz
// turns 503 as soon as shutdown begins. A health address that cannot be bound is a
// configuration error: the process exits before anything else listens.
func startHealth() *health {
	h := newHealth(logrus.StandardLogger())
	h.configValid()
	addr := envOr(healthAddrEnv, defaultHealthAddr)
	bound, err := serveHealth(h, addr)
	if err != nil {
		logrus.WithField("invalid_config", []string{healthAddrEnv}).
			Fatalf("invalid configuration, refusing to start: %s: cannot listen on %q: %v", healthAddrEnv, addr, err)
	}
	logrus.Infof("health listener on %s (/readyz, /livez)", bound)

	sigs := make(chan os.Signal, 1)
	signal.Notify(sigs, syscall.SIGTERM)
	go handleShutdown(h, sigs, os.Exit, reraiseSIGTERM)
	return h
}

// handleShutdown waits for SIGTERM and flips /readyz to 503 (shutdown) straight away. If
// smokescreen is serving, it received the same signal and drains its tunnels; the entry point
// returns once it has. If not (keys still loading, no allowlist yet, between reloads), there is
// nothing to drain and the process exits now.
//
// Smokescreen installs its own signal handler only once it is running, so a SIGTERM that lands
// while it is starting would be missed and the process would never stop. reraise closes that
// window: it sends one more SIGTERM a moment later, which a running smokescreen treats as the
// same, already-started shutdown.
func handleShutdown(h *health, sigs <-chan os.Signal, exit func(int), reraise func()) {
	<-sigs
	if h.startShutdown() {
		reraise()
		return
	}
	exit(0)
}

func reraiseSIGTERM() {
	time.Sleep(shutdownReraiseDelay)
	if p, err := os.FindProcess(os.Getpid()); err == nil {
		_ = p.Signal(syscall.SIGTERM)
	}
}
