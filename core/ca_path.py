r"""
Redirect mitm's CA_DIR to a writable, persistent location.

Without this, a PyInstaller --onefile bundle writes ca/ca.{crt,key} into
/tmp/_MEI.../ca/ — which is deleted when the process exits. That makes
the certificate-trust install fail because each launch generates a new
CA the user has never trusted.

Order of preference for CA_DIR:
  1. MRELAY_CA_DIR env var (set by the GUI: data\cert)
  2. <exe-dir>\ca  next to the bundled MasterRelayCore.exe
  3. <cwd>\ca     fallback for plain `python main_gui.py`

Must be imported BEFORE mitm.py.
"""

import os
import sys


def _resolve_ca_dir():
    env = os.environ.get("MRELAY_CA_DIR")
    if env:
        return os.path.abspath(env)

    if getattr(sys, "frozen", False):
        # PyInstaller: sys.executable is the real on-disk exe path
        return os.path.join(os.path.dirname(os.path.abspath(sys.executable)), "ca")

    return os.path.abspath(os.path.join(os.getcwd(), "ca"))


def install():
    ca_dir = _resolve_ca_dir()
    os.makedirs(ca_dir, exist_ok=True)

    import mitm
    mitm.CA_DIR = ca_dir
    mitm.CA_CERT_FILE = os.path.join(ca_dir, "ca.crt")
    mitm.CA_KEY_FILE = os.path.join(ca_dir, "ca.key")
    return ca_dir
