using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace OpenDock
{
    // "Stealth mode": every RGB light goes dark with one click, and comes back exactly as it was.
    // Talks to OpenRGB's SDK server (127.0.0.1:6742, open protocol). Before going dark it saves the current
    // lighting as an OpenRGB profile; turning stealth off loads that profile again.
    static class Stealth
    {
        const string Backup = "OpenDock stealth backup";
        const uint Version = 3;   // the controller layout we parse; the server answers in this version's format

        public static bool On { get { return Settings.StealthOn; } }

        // Runs off the UI thread; reports a short message (null on success).
        public static Task<string> Toggle()
        {
            bool turnOn = !Settings.StealthOn;
            return Task.Run(() =>
            {
                try
                {
                    using (var c = new Client())
                    {
                        if (turnOn)
                        {
                            c.Send(0, 151, Str(Backup));                    // REQUEST_SAVE_PROFILE
                            int n = c.Count();
                            for (uint i = 0; i < n; i++)
                            {
                                int leds = c.LedCount(i);
                                c.Send(i, 1100, new byte[0]);              // RGBCONTROLLER_SETCUSTOMMODE (direct)
                                var data = new byte[4 + 2 + 4 * leds];     // RGBCONTROLLER_UPDATELEDS, all black
                                BitConverter.GetBytes((uint)data.Length).CopyTo(data, 0);
                                BitConverter.GetBytes((ushort)leds).CopyTo(data, 4);
                                c.Send(i, 1050, data);
                            }
                        }
                        else c.Send(0, 152, Str(Backup));                   // REQUEST_LOAD_PROFILE
                        c.Count();   // round-trip so every command above has been processed before we close
                    }
                    Settings.StealthOn = turnOn;
                    Settings.Save();
                    return (string)null;
                }
                catch (SocketException) { return "OpenRGB isn't running. Start OpenRGB (with its SDK server on) to use Stealth Mode."; }
                catch (Exception ex) { App.Log(ex); return "Couldn't reach OpenRGB: " + ex.Message; }
            });
        }

        static byte[] Str(string s) { return Encoding.ASCII.GetBytes(s + "\0"); }

        class Client : IDisposable
        {
            readonly TcpClient tcp = new TcpClient();
            readonly NetworkStream ns;

            public Client()
            {
                var connect = tcp.ConnectAsync("127.0.0.1", 6742);
                if (!connect.Wait(1500)) throw new SocketException(10060);
                ns = tcp.GetStream();
                ns.ReadTimeout = ns.WriteTimeout = 4000;
                Send(0, 50, Str("OpenDock"));                                 // SET_CLIENT_NAME
                Send(0, 40, BitConverter.GetBytes(Version));                  // REQUEST_PROTOCOL_VERSION
                Receive();
            }

            public void Send(uint dev, uint id, byte[] data)
            {
                var h = new byte[16];
                Encoding.ASCII.GetBytes("ORGB").CopyTo(h, 0);
                BitConverter.GetBytes(dev).CopyTo(h, 4);
                BitConverter.GetBytes(id).CopyTo(h, 8);
                BitConverter.GetBytes((uint)data.Length).CopyTo(h, 12);
                ns.Write(h, 0, 16);
                ns.Write(data, 0, data.Length);
            }

            byte[] Receive()
            {
                var h = Read(16);
                return Read((int)BitConverter.ToUInt32(h, 12));
            }

            byte[] Read(int n)
            {
                var b = new byte[n];
                for (int o = 0; o < n; )
                {
                    int r = ns.Read(b, o, n - o);
                    if (r <= 0) throw new IOException("OpenRGB closed the connection");
                    o += r;
                }
                return b;
            }

            public int Count()
            {
                Send(0, 0, new byte[0]);                                      // REQUEST_CONTROLLER_COUNT
                return (int)BitConverter.ToUInt32(Receive(), 0);
            }

            // Walks the controller description (protocol v3 layout) to the colour count at its end.
            public int LedCount(uint dev)
            {
                Send(dev, 1, BitConverter.GetBytes(Version));                 // REQUEST_CONTROLLER_DATA
                var r = new BinaryReader(new MemoryStream(Receive()));
                Action str = () => r.ReadBytes(r.ReadUInt16());
                r.ReadUInt32(); r.ReadInt32();                                // size, type
                for (int s = 0; s < 6; s++) str();                            // name, vendor, description, version, serial, location
                int modes = r.ReadUInt16(); r.ReadInt32();                    // active mode
                for (int m = 0; m < modes; m++) { str(); r.ReadBytes(4 * 12); r.ReadBytes(4 * r.ReadUInt16()); }
                int zones = r.ReadUInt16();
                for (int z = 0; z < zones; z++) { str(); r.ReadBytes(16); r.ReadBytes(r.ReadUInt16()); }
                int leds = r.ReadUInt16();
                for (int l = 0; l < leds; l++) { str(); r.ReadUInt32(); }
                return r.ReadUInt16();
            }

            public void Dispose() { tcp.Close(); }
        }
    }
}
