"""
CasaTimo — Setup token Viessmann (esegui UNA VOLTA sul tuo PC con browser).

Questo script apre il browser per autenticarti con il tuo account Viessmann/ViCare,
poi salva il token in data/viessmann_token.json.
Il container Docker userà quel file per accedere all'API senza aprire browser.

Uso:
    pip install PyViCare
    python setup_token.py
"""

import os
import sys
from pathlib import Path
from PyViCare.PyViCare import PyViCare

TOKEN_DIR  = Path(__file__).parent / "data"
TOKEN_FILE = TOKEN_DIR / "viessmann_token.json"

def main():
    print("=" * 55)
    print("  Setup autenticazione Viessmann — CasaTimo")
    print("=" * 55)

    # Leggi credenziali da env o chiedi interattivamente
    user = os.getenv("VIESSMANN_USER") or input("\nEmail account ViCare: ").strip()
    pwd  = os.getenv("VIESSMANN_PASS") or input("Password ViCare:       ").strip()

    client_id = os.getenv("VIESSMANN_CLIENT_ID") or input(
        "Client ID [invio = viessmann.vicare.android]: "
    ).strip() or "viessmann.vicare.android"

    if not user or not pwd:
        print("ERRORE: email e password sono obbligatorie.")
        sys.exit(1)

    TOKEN_DIR.mkdir(exist_ok=True)
    print(f"\nToken verrà salvato in: {TOKEN_FILE}")
    print("\nSi aprirà il browser per il login Viessmann.")
    print("Dopo aver fatto login, il browser verrà rediretto su localhost:4200.")
    print("Lascia aperta questa finestra finché non vedi 'Token salvato'.\n")

    try:
        import logging
        logging.basicConfig(level=logging.DEBUG)
        vicare = PyViCare()
        vicare.initWithCredentials(user, pwd, client_id, str(TOKEN_FILE))

        print("\n✅ Token salvato con successo!")
        print(f"   File: {TOKEN_FILE}")
        print(f"\nDispositivi trovati: {len(vicare.devices)}")
        for d in vicare.devices:
            print(f"   - {d.getModel()}")

        print("\nAdesso puoi avviare il container Docker:")
        print("   docker-compose up -d viessmann-sidecar")

    except Exception as e:
        import traceback
        print(f"\n❌ Errore durante l'autenticazione:\n   {e}")
        print("\n--- Traceback completo ---")
        traceback.print_exc()
        sys.exit(1)

if __name__ == "__main__":
    main()
