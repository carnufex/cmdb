# Build context: src/web
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npx ng build

FROM nginxinc/nginx-unprivileged:1.29-alpine
ENV API_UPSTREAM=http://api:8080 \
    OIDC_AUTHORITY=https://authentik.rosenvall.se/application/o/cmdb/ \
    OIDC_CLIENT_ID=cmdb-web \
    LANTMATERIET_KEY="" \
    NGINX_ENVSUBST_FILTER=^(API_|OIDC_|LANTMATERIET_)
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist/web/browser /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s CMD wget -q --spider http://127.0.0.1:8080/ || exit 1
