package main

import (
	"encoding/base64"
	"net/http"
	"testing"

	"github.com/golang-jwt/jwt/v5"
)

// The role claim differs by Entra token version: v1/managed-identity tokens use `appid`,
// v2 tokens use `azp`. appIDFromClaims must accept either (appid preferred) and reject a
// token carrying neither.
func TestAppIDFromClaims(t *testing.T) {
	cases := []struct {
		name   string
		claims jwt.MapClaims
		want   string
		wantOK bool
	}{
		{"v1/MI appid", jwt.MapClaims{"appid": "module-a"}, "module-a", true},
		{"v2 azp", jwt.MapClaims{"azp": "module-b"}, "module-b", true},
		{"appid preferred over azp", jwt.MapClaims{"appid": "a", "azp": "b"}, "a", true},
		{"neither present", jwt.MapClaims{"sub": "x"}, "", false},
		{"empty appid falls back to azp", jwt.MapClaims{"appid": "", "azp": "c"}, "c", true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := appIDFromClaims(c.claims)
			if (err == nil) != c.wantOK || got != c.want {
				t.Errorf("got (%q, err=%v), want (%q, ok=%v)", got, err, c.want, c.wantOK)
			}
		})
	}
}

// basicProxyCreds is the shared parser for the basic-name/basic-jwt spike identity models.
// A malformed or absent header must yield ok=false so the role funcs return MissingRoleError
// (-> 407 Basic challenge), never a half-parsed credential.
func TestBasicProxyCreds(t *testing.T) {
	mk := func(h string) *http.Request {
		r, _ := http.NewRequest("CONNECT", "https://example.com:443", nil)
		if h != "" {
			r.Header.Set("Proxy-Authorization", h)
		}
		return r
	}
	enc := func(s string) string { return "Basic " + base64.StdEncoding.EncodeToString([]byte(s)) }

	cases := []struct {
		name             string
		header           string
		wantOK           bool
		wantUser, wantPw string
	}{
		{"service name + filler pw", enc("module-a:x"), true, "module-a", "x"},
		{"jwt in password", enc("module-a:aaa.bbb.ccc"), true, "module-a", "aaa.bbb.ccc"},
		{"password may contain colons", enc("u:a:b:c"), true, "u", "a:b:c"},
		{"empty username", enc(":tok"), true, "", "tok"},
		{"absent header", "", false, "", ""},
		{"bearer not basic", "Bearer aaa.bbb.ccc", false, "", ""},
		{"not base64", "Basic !!!notb64", false, "", ""},
		{"no colon", enc("nopassword"), false, "", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			u, p, ok := basicProxyCreds(mk(c.header))
			if ok != c.wantOK || u != c.wantUser || p != c.wantPw {
				t.Errorf("got (%q,%q,%v), want (%q,%q,%v)", u, p, ok, c.wantUser, c.wantPw, c.wantOK)
			}
		})
	}
}

// The challenge header must appear only on a credential-less 407, so compliant clients send
// Basic creds once but a denied-with-creds response (bad token / disallowed host) doesn't loop.
func TestBasicChallengeHandler(t *testing.T) {
	resp := func(code int, hadCreds bool) *http.Response {
		r, _ := http.NewRequest("CONNECT", "https://example.com:443", nil)
		if hadCreds {
			r.Header.Set("Proxy-Authorization", "Basic dTpw")
		}
		return &http.Response{StatusCode: code, Header: make(http.Header), Request: r}
	}

	if r := resp(http.StatusProxyAuthRequired, false); func() bool {
		basicChallengeHandler(nil, r)
		return r.Header.Get("Proxy-Authenticate") == ""
	}() {
		t.Error("expected Basic challenge on credential-less 407")
	}
	if r := resp(http.StatusProxyAuthRequired, true); func() bool {
		basicChallengeHandler(nil, r)
		return r.Header.Get("Proxy-Authenticate") != ""
	}() {
		t.Error("must not re-challenge a 407 that already presented creds")
	}
	if r := resp(http.StatusOK, false); func() bool {
		basicChallengeHandler(nil, r)
		return r.Header.Get("Proxy-Authenticate") != ""
	}() {
		t.Error("must not add a challenge to a non-407 response")
	}
}
