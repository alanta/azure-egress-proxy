package main

import (
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"fmt"
	"math/big"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

// testJWKS returns a one-key RS256 JWKS document and the matching private key.
func testJWKS(t *testing.T) (string, *rsa.PrivateKey) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	b64 := base64.RawURLEncoding.EncodeToString
	doc := fmt.Sprintf(`{"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"k1","n":%q,"e":%q}]}`,
		b64(key.N.Bytes()), b64(big.NewInt(int64(key.E)).Bytes()))
	return doc, key
}

// A JWKS that never loads must not yield a validator: keyfunc's defaults would hand back an
// empty key set, and the proxy would serve while rejecting every token (issue #79).
func TestLoadJWKSFailsWithoutKeys(t *testing.T) {
	unreachable := httptest.NewServer(http.NotFoundHandler())
	unreachableURL := unreachable.URL + "/keys"
	unreachable.Close()

	cases := []struct {
		name    string
		url     string
		handler http.HandlerFunc
		wantErr string
	}{
		{name: "unreachable", url: unreachableURL, wantErr: "failed to perform HTTP request"},
		{name: "non-200", handler: func(w http.ResponseWriter, _ *http.Request) {
			http.Error(w, "nope", http.StatusServiceUnavailable)
		}, wantErr: "503"},
		{name: "bad JSON", handler: func(w http.ResponseWriter, _ *http.Request) {
			fmt.Fprint(w, "<html>")
		}, wantErr: "decode"},
		{name: "empty key set", handler: func(w http.ResponseWriter, _ *http.Request) {
			fmt.Fprint(w, `{"keys":[]}`)
		}, wantErr: "no usable signing keys"},
		{name: "only unsupported keys", handler: func(w http.ResponseWriter, _ *http.Request) {
			fmt.Fprint(w, `{"keys":[{"kty":"nope","kid":"x"}]}`)
		}, wantErr: "no usable signing keys"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			u := tc.url
			if tc.handler != nil {
				srv := httptest.NewServer(tc.handler)
				defer srv.Close()
				u = srv.URL
			}
			k, stop, err := loadJWKS(u, 2, time.Millisecond)
			if err == nil {
				stop()
				t.Fatal("loadJWKS succeeded, want an error")
			}
			if k != nil {
				t.Error("loadJWKS returned a Keyfunc alongside its error")
			}
			if !strings.Contains(err.Error(), tc.wantErr) {
				t.Errorf("error = %q, want it to mention %q", err, tc.wantErr)
			}
		})
	}
}

// The IdP may come up after the proxy: a JWKS that fails first and then serves keys loads.
func TestLoadJWKSRetriesUntilKeys(t *testing.T) {
	doc, key := testJWKS(t)
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		if calls.Add(1) < 3 {
			http.Error(w, "starting", http.StatusServiceUnavailable)
			return
		}
		fmt.Fprint(w, doc)
	}))
	defer srv.Close()

	k, stop, err := loadJWKS(srv.URL, 5, time.Millisecond)
	if err != nil {
		t.Fatalf("loadJWKS: %v", err)
	}
	defer stop()

	tok := jwt.NewWithClaims(jwt.SigningMethodRS256, jwt.MapClaims{"appid": "a"})
	tok.Header["kid"] = "k1"
	signed, err := tok.SignedString(key)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := jwt.Parse(signed, k.Keyfunc); err != nil {
		t.Errorf("token signed with the served key did not verify: %v", err)
	}
}
