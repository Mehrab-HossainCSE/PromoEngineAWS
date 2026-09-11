# syntax=docker/dockerfile:1

# =============================================================================
# PromoEngine frontend - production image
#
# Build context: PromoFrontend/
#   docker build -f PromoFrontend/Dockerfile PromoFrontend
#
# Stage 1 compiles the Angular application with the Node toolchain; stage 2
# keeps only the emitted static files and serves them from Nginx. Node and
# node_modules never reach the production image.
#
# No environment-specific value is baked in here. The production build uses
# environment.production.ts, whose apiUrl is '' - every API call is therefore a
# same-origin /api/... request that Nginx proxies to the backend container.
# =============================================================================

# ---------- stage 1: build ----------
FROM node:22-alpine AS build
WORKDIR /app

# The lockfile is copied alone first so `npm ci` caches as its own layer and is
# only redone when dependencies change.
COPY package.json package-lock.json ./

# `npm ci` installs exactly what package-lock.json pins, which is what makes a
# CI build reproducible. devDependencies are required: the Angular compiler
# lives there.
RUN --mount=type=cache,target=/root/.npm \
    npm ci --no-audit --no-fund

COPY . .

# defaultConfiguration is "production" in angular.json, which applies the
# fileReplacement to environment.production.ts, optimisation and output hashing.
RUN npm run build -- --configuration=production

# ---------- stage 2: serve ----------
FROM nginx:1.27-alpine AS final

# Replace the stock server block with the SPA + API-proxy configuration.
RUN rm -f /etc/nginx/conf.d/default.conf
COPY nginx/default.conf /etc/nginx/conf.d/default.conf

# The Angular application builder emits the browser bundle under
# dist/<project>/browser. The project is PromoFrontend (see angular.json).
COPY --from=build /app/dist/PromoFrontend/browser /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost/healthz || exit 1

CMD ["nginx", "-g", "daemon off;"]
