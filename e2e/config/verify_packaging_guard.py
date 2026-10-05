"""Exercise the actual ZIP guard against valid, missing, malformed, conflicting settings."""
import copy
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
HOST = ROOT / 'DeChimexPrints/src/DeChimex.Host'
script = (ROOT / 'DeChimexPrints/tools/make-deliverable-zip.sh').read_text()
guard = script.split("<<'PYGUARD'\n", 1)[1].split('\nPYGUARD', 1)[0]
names = ['appsettings.json', 'appsettings.Development.json']
original = [json.loads((HOST / n).read_text()) for n in names]
checks = 0

def run(data, valid, name):
    global checks
    with tempfile.TemporaryDirectory() as tmp:
        host = Path(tmp) / 'DeChimexPrints/src/DeChimex.Host'
        host.mkdir(parents=True)
        for n, d in zip(names, data):
            (host / n).write_text(json.dumps(d))
        result = subprocess.run(['python3', '-c', guard, tmp], capture_output=True)
        assert (result.returncode == 0) == valid, name
    checks += 1
    print(f'PASS {checks:02}: {name}')

run(original, True, 'matching supplied keys accepted')
for i in range(2):
    for section, key in [('Brevo','ApiKey'), ('Paystack','SecretKey'), ('Paystack','PublicKey')]:
        for value, label in [('', 'empty'), ('wrong', 'malformed')]:
            data = copy.deepcopy(original)
            data[i][section][key] = value
            run(data, False, f'{names[i]} {section}.{key} {label} rejected')
data = copy.deepcopy(original)
key = data[1]['Paystack']['SecretKey']
data[1]['Paystack']['SecretKey'] = key[:-1] + ('0' if key[-1] != '0' else '1')
run(data, False, 'well-formed but conflicting keys rejected')
print(f'ALL {checks} PACKAGING VALIDATIONS PASSED; no values logged')
