package main

import (
	"encoding/base64"
	"fmt"
	"net/http"
	"net/http/httptest"
	"runtime"
	"sync/atomic"
	"testing"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

// Managed mode must route every identity mode, not only netid: basic-name resolves the
// Basic username with no env/JWKS dependency, and the default (netid) derives its subnet
// map from the fetched modules. (jwt/basic-jwt are covered by the JWKS-reuse test below.)
func TestManagedRoleFromRequestBasicName(t *testing.T) {
	f := newManagedRoleFunc("basic-name")(nil)
	req, _ := http.NewRequest("CONNECT", "https://example.com:443", nil)
	req.Header.Set("Proxy-Authorization",
		"Basic "+base64.StdEncoding.EncodeToString([]byte("sample-app:x")))
	role, err := f(req)
	if err != nil || role != "sample-app" {
		t.Errorf("got (%q, %v), want (sample-app, nil)", role, err)
	}
}

func TestManagedRoleFromRequestNetIDDefault(t *testing.T) {
	mods := []module{
		{ID: "mod-a", Subnet: "172.30.10.0/24"},
		{ID: "no-subnet"}, // token-mode module: skipped by the subnet map, not fatal
	}
	f := newManagedRoleFunc("netid")(mods)

	req, _ := http.NewRequest("CONNECT", "https://example.com:443", nil)
	req.RemoteAddr = "172.30.10.7:52011"
	if role, err := f(req); err != nil || role != "mod-a" {
		t.Errorf("in-subnet: got (%q, %v), want (mod-a, nil)", role, err)
	}

	req.RemoteAddr = "10.9.9.9:52011"
	if _, err := f(req); err == nil {
		t.Error("out-of-subnet source must not resolve to a role")
	}
}

// The token modes build their JWKS client once, not per allowlist reload: reloads must not
// re-fetch the JWKS or leave a refresh goroutine behind per reload (issue #80).
func TestManagedRoleFuncReusesJWKSAcrossReloads(t *testing.T) {
	doc, key := testJWKS(t)
	var fetches atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		fetches.Add(1)
		fmt.Fprint(w, doc)
	}))
	defer srv.Close()
	t.Setenv("JWKS_URL", srv.URL)
	t.Setenv("EXPECT_ISS", "https://issuer.test/")
	t.Setenv("EXPECT_AUD", "egress-proxy")

	now := time.Now()
	tok := jwt.NewWithClaims(jwt.SigningMethodRS256, jwt.MapClaims{
		"iss": "https://issuer.test/", "aud": "egress-proxy", "appid": "mod-a",
		"iat": now.Unix(), "exp": now.Add(time.Minute).Unix(),
	})
	tok.Header["kid"] = "k1"
	signed, err := tok.SignedString(key)
	if err != nil {
		t.Fatal(err)
	}

	for _, mode := range []string{"jwt", "basic-jwt"} {
		t.Run(mode, func(t *testing.T) {
			fetches.Store(0)
			roleFor := newManagedRoleFunc(mode)
			before := runtime.NumGoroutine()

			var f func(*http.Request) (string, error)
			for i := 0; i < 5; i++ {
				f = roleFor([]module{{ID: fmt.Sprintf("mod-%d", i)}})
			}

			if n := fetches.Load(); n != 1 {
				t.Errorf("JWKS fetched %d times over 5 reloads, want 1", n)
			}
			if after := runtime.NumGoroutine(); after > before {
				t.Errorf("goroutines grew from %d to %d over 5 reloads", before, after)
			}

			req, _ := http.NewRequest("CONNECT", "https://example.com:443", nil)
			if mode == "jwt" {
				req.Header.Set("Proxy-Authorization", "Bearer "+signed)
			} else {
				req.Header.Set("Proxy-Authorization",
					"Basic "+base64.StdEncoding.EncodeToString([]byte("mod-a:"+signed)))
			}
			if role, err := f(req); err != nil || role != "mod-a" {
				t.Errorf("got (%q, %v), want (mod-a, nil)", role, err)
			}
		})
	}
}
