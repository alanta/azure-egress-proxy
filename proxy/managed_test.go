package main

import (
	"encoding/base64"
	"net/http"
	"testing"
)

// Managed mode must route every identity mode, not only netid: basic-name resolves the
// Basic username with no env/JWKS dependency, and the default (netid) derives its subnet
// map from the fetched modules. (jwt/basic-jwt need a live JWKS and are covered by the
// integration stack, not unit tests.)
func TestManagedRoleFromRequestBasicName(t *testing.T) {
	f := managedRoleFromRequest("basic-name", nil)
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
	f := managedRoleFromRequest("netid", mods)

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
