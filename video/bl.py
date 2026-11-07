# Sends a Python file to the running Blender through the BlenderMCP add-on (port 9876) and prints the reply.
#   python video/bl.py some_script.py
# The script runs inside Blender; whatever it assigns to `result` is not returned by this add-on, so scripts
# print() what they want to report (the add-on sends back stdout).
import socket, json, sys

code = open(sys.argv[1], encoding="utf-8").read()
s = socket.create_connection(("127.0.0.1", 9876), timeout=10)
s.settimeout(600)
s.sendall(json.dumps({"type": "execute_code", "params": {"code": code}}).encode())
data = b""
while True:
    chunk = s.recv(65536)
    if not chunk:
        break
    data += chunk
    try:
        reply = json.loads(data)
        break
    except ValueError:
        continue
s.close()
r = reply.get("result", reply)
if isinstance(r, dict) and "result" in r:
    r = r["result"]
print(r if isinstance(r, str) else json.dumps(r, indent=1)[:4000])
