# Multi-stage build for the RWK relay server.
#
# Stage 1 builds the React dashboard with Node. Stage 2 builds the Rust binary (static-ish,
# against the glibc in the runtime image). Stage 3 is a slim Debian runtime holding just the
# binary and the built dashboard. The result is one small image that runs the relay protocol
# and the metrics dashboard in a single process.
#
# Build context is the `rust/` workspace root (so the build can see the whole Cargo workspace):
#   docker build -f crates/rwk-relay/Dockerfile -t rwk-relay .

# ---- Stage 1: build the dashboard ----
FROM node:22-bookworm-slim AS web
WORKDIR /web
# Install deps against the lockfile first so this layer caches across source edits.
COPY crates/rwk-relay/web/package*.json ./
RUN npm install
COPY crates/rwk-relay/web/ ./
RUN npm run build
# -> /web/dist

# ---- Stage 2: build the Rust binary ----
FROM rust:1-bookworm AS build
WORKDIR /src
# The relay crate depends on rwk-core, which pulls in serialport (needs libudev) and ring
# (needs a C compiler, present in the rust image). cpal/alsa are only needed by the audio
# path, which the relay binary does not touch — but rwk-core references them, so install the
# dev headers so the whole workspace compiles.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libudev-dev libasound2-dev pkg-config \
    && rm -rf /var/lib/apt/lists/*
# Copy the whole workspace (relay reuses rwk-core).
COPY . .
RUN cargo build --release -p rwk-relay
# -> /src/target/release/rwk-relay

# ---- Stage 3: slim runtime ----
FROM debian:bookworm-slim AS runtime
# Runtime libs for serialport (libudev) and alsa, pulled in transitively even though the
# relay does not use them; ca-certificates for good measure.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libudev1 libasound2 ca-certificates wget \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /src/target/release/rwk-relay /app/rwk-relay
COPY --from=web /web/dist /app/web/dist
ENV RWK_WEB_ROOT=/app/web/dist \
    RWK_RELAY_ADDR=0.0.0.0:9000 \
    RWK_HTTP_ADDR=0.0.0.0:8080
# 9000: the relay/rendezvous protocol (nodes connect here).
# 8080: the dashboard + API (put Cloudflare / a reverse proxy in front for TLS).
EXPOSE 9000 8080
# A non-root user: the relay needs no privileges.
RUN useradd --system --no-create-home rwk && chown -R rwk /app
USER rwk
ENTRYPOINT ["/app/rwk-relay"]
