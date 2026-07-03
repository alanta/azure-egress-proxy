# Build the egress-proxy binary (embeds Stripe Smokescreen as a library and supplies the
# Azure-native RoleFromRequest identity modes). See main.go and docs/identity.md.
# The build stage runs on the build platform and cross-compiles to the target arch, so a
# multi-platform buildx build never emulates the Go toolchain (vet/test run once, natively).
FROM --platform=$BUILDPLATFORM golang:1.25-alpine AS build
RUN apk add --no-cache git
ENV CGO_ENABLED=0
ARG TARGETOS TARGETARCH
WORKDIR /src
# Dependencies are pinned in the committed go.mod/go.sum (reproducible builds, no floating
# @latest). Copy them first so `go mod download` is cached when only source changes.
COPY go.mod go.sum ./
RUN go mod download
COPY *.go ./
# Tests run as part of the build: a vet/test failure fails the image build (the dev cycle).
RUN go vet ./... \
 && go test ./... \
 && GOOS=$TARGETOS GOARCH=$TARGETARCH go build -o /egress-proxy .
# Writable render dir for managed mode, pre-owned by the distroless nonroot uid (65532)
# so the binary can write /render/acl.yaml without running as root.
RUN mkdir -p /render

# Locked-down runtime: distroless static (no shell, no package manager), running as the
# nonroot user. The binary is a static CGO-free build, and distroless/static ships the CA
# bundle + tzdata, so nothing else is needed.
FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /egress-proxy /usr/bin/egress-proxy
COPY --from=build --chown=65532:65532 /render /render
ENTRYPOINT ["egress-proxy"]
