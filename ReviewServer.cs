using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PdfQualityCompare
{
    /// <summary>
    /// Serves the report on this machine so verdicts can be recorded from it. The report is
    /// the same page either way; served from here it finds the /api endpoint and turns its
    /// review buttons on, opened as a file it is read-only.
    ///
    /// A bare socket rather than HttpListener, which needs a URL reservation or an
    /// elevated prompt on Windows. Bound to the loopback address only, and it serves the
    /// report and its crops and nothing else.
    /// </summary>
    internal static class ReviewServer
    {
        private static readonly Regex CropPath = new Regex(@"^/review/item-\d+\.png$", RegexOptions.Compiled);

        /// <summary>Called after every verdict, to write the report and CSV out again.</summary>
        public delegate void Rewrite();

        public static void Serve(ReviewState state, string reportPath, int port, Rewrite rewrite)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            int actual = ((IPEndPoint)listener.LocalEndpoint).Port;
            string url = "http://127.0.0.1:" + actual + "/";

            Console.WriteLine();
            Console.WriteLine("Review    : {0}", url);
            Console.WriteLine("            verdicts are saved to {0}", state.BaselinePath);
            Console.WriteLine("            press Ctrl+C to stop");
            // Not when the output is captured: a scripted run has nobody at the browser.
            if (!Console.IsOutputRedirected)
            {
                try { Process.Start(url); } catch (Exception) { /* the URL is printed above */ }
            }

            while (true)
            {
                using (TcpClient client = listener.AcceptTcpClient())
                {
                    try { Handle(client, state, reportPath, rewrite); }
                    catch (Exception ex) { Console.Error.WriteLine("Review    : request failed: " + ex.Message); }
                }
            }
        }

        private static void Handle(TcpClient client, ReviewState state, string reportPath, Rewrite rewrite)
        {
            client.ReceiveTimeout = 10000;
            NetworkStream stream = client.GetStream();

            string method, path;
            Dictionary<string, string> headers;
            if (!ReadHead(stream, out method, out path, out headers)) return;

            string lengthText;
            int length = 0;
            if (headers.TryGetValue("content-length", out lengthText)) int.TryParse(lengthText, out length);
            byte[] body = ReadBody(stream, Math.Min(length, 1 << 20));

            int query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);

            // Another web page open in the same browser can reach a loopback port too. A
            // Host other than this one is a page that rebound its own name to us, and a
            // verdict sent as anything but JSON is a form posted from elsewhere - a page
            // cannot send JSON here without a preflight this server never answers.
            string host;
            int port = ((IPEndPoint)client.Client.LocalEndPoint).Port;
            if (!headers.TryGetValue("host", out host) ||
                (host != "127.0.0.1:" + port && host != "localhost:" + port))
            {
                Send(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"));
                return;
            }

            if (method == "GET" && (path == "/" || path == "/report.html"))
            {
                Send(stream, 200, "text/html; charset=utf-8", File.ReadAllBytes(reportPath));
            }
            else if (method == "GET" && path == "/api/ping")
            {
                Send(stream, 200, "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
            }
            else if (method == "GET" && CropPath.IsMatch(path))
            {
                string file = Path.Combine(state.OutputDir, "review", Path.GetFileName(path));
                if (File.Exists(file)) Send(stream, 200, "image/png", File.ReadAllBytes(file));
                else Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
            }
            else if (method == "POST" && path == "/api/verdict")
            {
                string type;
                if (!headers.TryGetValue("content-type", out type) ||
                    !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    Send(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"));
                    return;
                }
                Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(Verdict(state, body, rewrite)));
            }
            else
            {
                Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
            }
        }

        private static string Verdict(ReviewState state, byte[] body, Rewrite rewrite)
        {
            var json = new JavaScriptSerializer();
            string error;
            ReviewItem changed = null;

            lock (state.Sync)
            {
                try
                {
                    var request = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                    int number = Convert.ToInt32(request["number"]);
                    object verdict, truth;
                    request.TryGetValue("verdict", out verdict);
                    request.TryGetValue("truth", out truth);

                    error = ReviewSet.SetVerdict(state, number, verdict as string, truth as string);
                    if (error == null)
                    {
                        changed = state.Items.Find(i => i.Number == number);
                        rewrite();
                        Console.WriteLine("Review    : #{0} {1} - {2}", number,
                                          changed.Verdict ?? "cleared", ReviewSet.Describe(state));
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            if (error != null) return "{\"error\":" + ReportWriter.Js(error) + "}";
            return "{\"item\":" + ReportWriter.ReviewItemJson(changed) +
                   ",\"score\":" + ReportWriter.ReviewScoreJson(state) + "}";
        }

        private static bool ReadHead(NetworkStream stream, out string method, out string path,
                                     out Dictionary<string, string> headers)
        {
            method = path = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string request = ReadLine(stream);
            if (string.IsNullOrEmpty(request)) return false;
            string[] parts = request.Split(' ');
            if (parts.Length < 2) return false;
            method = parts[0].ToUpperInvariant();
            path = Uri.UnescapeDataString(parts[1]);

            for (string line = ReadLine(stream); !string.IsNullOrEmpty(line); line = ReadLine(stream))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            return true;
        }

        private static string ReadLine(NetworkStream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 8192)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static byte[] ReadBody(NetworkStream stream, int length)
        {
            var body = new byte[Math.Max(0, length)];
            int read = 0;
            while (read < body.Length)
            {
                int n = stream.Read(body, read, body.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return body;
        }

        private static void Send(NetworkStream stream, int status, string type, byte[] content)
        {
            string reason = status == 200 ? "OK" : (status == 403 ? "Forbidden" : "Not Found");
            string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                          "Content-Type: " + type + "\r\n" +
                          "Content-Length: " + content.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
        }
    }
}
