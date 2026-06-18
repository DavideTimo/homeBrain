"""
CasaTimo — Setup token Viessmann (esegui UNA VOLTA sul tuo PC).
Autentica senza browser via PKCE + HTTP Basic auth e salva il token.
"""
import base64, hashlib, json, os, secrets, sys, time
from pathlib import Path
from urllib.parse import parse_qs, urlparse
import requests

TOKEN_DIR  = Path(__file__).parent / "data"
TOKEN_FILE = TOKEN_DIR / "viessmann_token.json"

AUTHORIZE_URL = "https://iam.viessmann-climatesolutions.com/idp/v3/authorize"
TOKEN_URL     = "https://iam.viessmann-climatesolutions.com/idp/v3/token"
REDIRECT_URI  = "vicare://oauth-callback/everest"


def pkce_pair():
    v = base64.urlsafe_b64encode(secrets.token_bytes(48)).rstrip(b"=").decode()
    c = base64.urlsafe_b64encode(hashlib.sha256(v.encode()).digest()).rstrip(b"=").decode()
    return v, c


def main():
    print("=" * 55)
    print("  Setup autenticazione Viessmann — CasaTimo")
    print("=" * 55)

    user      = os.getenv("VIESSMANN_USER")      or input("\nEmail ViCare:  ").strip()
    pwd       = os.getenv("VIESSMANN_PASS")      or input("Password:       ").strip()
    client_id = os.getenv("VIESSMANN_CLIENT_ID") or input("Client ID:      ").strip()

    if not all([user, pwd, client_id]):
        print("ERRORE: tutti i campi sono obbligatori."); sys.exit(1)

    TOKEN_DIR.mkdir(exist_ok=True)
    print(f"\nToken → {TOKEN_FILE}")
    print("Autenticazione in corso (nessun browser richiesto)...\n")

    verifier, challenge = pkce_pair()
    params = {
        "response_type":         "code",
        "client_id":             client_id,
        "redirect_uri":          REDIRECT_URI,
        "scope":                 "IoT offline_access",
        "code_challenge":        challenge,
        "code_challenge_method": "S256",
    }

    resp = requests.post(
        AUTHORIZE_URL, params=params,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        auth=(user, pwd), allow_redirects=False, timeout=15,
    )
    print(f"Server auth → HTTP {resp.status_code}")
    if resp.text:
        print(f"Body: {resp.text[:300]}")

    if resp.status_code != 302 or "Location" not in resp.headers:
        print(f"\n❌ Autenticazione fallita: HTTP {resp.status_code}")
        sys.exit(1)

    location = resp.headers["Location"]
    parsed   = urlparse(location)
    code     = (parse_qs(parsed.query).get("code") or
                parse_qs(parsed.fragment).get("code") or [None])[0]
    if not code:
        print(f"❌ Code non trovato: {location[:200]}"); sys.exit(1)

    print("Scambio codice per token...")
    tok_resp = requests.post(TOKEN_URL, data={
        "grant_type":    "authorization_code",
        "code":          code,
        "client_id":     client_id,
        "redirect_uri":  REDIRECT_URI,
        "code_verifier": verifier,
    }, timeout=15)

    if not tok_resp.ok:
        print(f"❌ Token exchange fallito: HTTP {tok_resp.status_code}\n{tok_resp.text[:300]}")
        sys.exit(1)

    token = tok_resp.json()
    token["obtained_at"] = time.time()
    TOKEN_FILE.write_text(json.dumps(token, indent=2))

    print(f"\n✅ Token salvato!")
    print(f"   Scope:         {token.get('scope','n/a')}")
    print(f"   Scade tra:     {token.get('expires_in','?')}s")
    print(f"   Refresh token: {'✅' if 'refresh_token' in token else '❌ assente'}")
    print("\nAdesso puoi avviare: docker-compose up -d viessmann-sidecar")


if __name__ == "__main__":
    main()
