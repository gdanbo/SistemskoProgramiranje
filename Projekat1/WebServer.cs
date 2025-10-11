using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;

public class WebServer
{
    private readonly HttpListener listener = new HttpListener();
    private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
    private readonly object cacheLock = new object();
    private bool running = false;

    public WebServer(string prefix)
    {
        listener.Prefixes.Add(prefix);
    }

    public void Start()
    {
        running = true;
        listener.Start();
        ThreadPool.QueueUserWorkItem(ListenLoop);
    }

    private void ListenLoop(object state)
    {
        while (running)
        {
            try
            {
                HttpListenerContext context = listener.GetContext();
                ThreadPool.QueueUserWorkItem(ProcessRequest, context);
            }
            catch (HttpListenerException)
            {
                break;
            }
        }
    }

    private void ProcessRequest(object ctxObj)
    {
        var ctx = (HttpListenerContext)ctxObj;
        var req = ctx.Request;
        var resp = ctx.Response;

        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm}] {req.HttpMethod} {req.Url}");

        try
        {
            // proveravamo jel dobar zahtev
            if (req.HttpMethod != "GET" || req.Url.AbsolutePath != "/search")
            {
                WriteJson(resp, 405, "{\"error\": \"Koristiti GET metodu sa /search?q=...\"}");
                return;
            }

            string? query = req.QueryString["q"];
            if (string.IsNullOrEmpty(query))
            {
                WriteJson(resp, 400, "{\"error\": \"Nedostaje parametar q.\"}");
                return;
            }

            string googleUrl = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}";
            string result;

            lock (cacheLock)
            {
                if (cache.ContainsKey(googleUrl)) // ako u cache vec imamo odgovor tog requesta vracamo odmah njega
                {
                    result = cache[googleUrl];
                    Console.WriteLine($"Pronađeno u kešu: {result}");
                    WriteJson(resp, 200, result);
                    return;
                }
            }

            var client = new WebClient();
            try
            {
                result = client.DownloadString(googleUrl);
            }
            catch (WebException ex)
            {
                string err = $"{{\"error\": \"Google Books API greška: {ex.Message}\"}}";
                WriteJson(resp, 502, err);
                return;
            }

            if (!result.Contains("\"items\""))
            {
                WriteJson(resp, 404, "{\"error\": \"Nije pronađena nijedna knjiga.\"}");
                return;
            }

            lock (cacheLock)
            {
                if (!cache.ContainsKey(googleUrl))
                {
                    cache.Add(googleUrl, result);
                }
            }

            WriteJson(resp, 200, result);
        }
        catch (Exception ex)
        {
            WriteJson(resp, 500, $"{{\"error\": \"Greška: {ex.Message}\"}}");
        }
        finally
        {
            resp.OutputStream.Close();
        }
    }

    private void WriteJson(HttpListenerResponse resp, int status, string body)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(body);
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = buffer.Length;
        resp.OutputStream.Write(buffer, 0, buffer.Length); // mora ovako
    }
}
