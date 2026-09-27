Authentik-blueprints (YAML) som monteras i `/blueprints/custom` och appliceras av workern vid start och vid ändring.

[`cmdb.yaml`](cmdb.yaml) skapar:

- Den publika OAuth2-klienten `cmdb-web` (authorization code + PKCE, refresh tokens). Redirect-URI:er finns för compose (`CMDB_WEB_PORT`) och `ng serve` (4200).
- Applikationen `cmdb`, med issuer `http://localhost:9000/application/o/cmdb/`.
- Grupperna `cmdb-full`, `cmdb-region-nord` och `cmdb-projekt-a` samt demoanvändarna `demo-full`, `demo-region` och `demo-projekt`. Lösenordet läses från `CMDB_DEMO_PASSWORD`.

Grupperna finns i tokenets `groups`-claim (scope `profile`) och mappas till behörighetsomfång i #22.

Applicera direkt efter en ändring:

```bash
docker compose exec authentik-worker ak apply_blueprint /blueprints/custom/cmdb.yaml
```
