using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

public class ReactiveWebServer
{
    private readonly HttpListener listener = new();
    private readonly HttpClient http = new();

    public ReactiveWebServer(string prefix)
    {
        listener.Prefixes.Add(prefix);
    }
    public async Task StartAsync()
    {
        listener.Start();
        Console.WriteLine("Listener startovan");

        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var url = req.Url?.AbsolutePath ?? "";

        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm}] {req.HttpMethod} {req.Url}");

        if (req.HttpMethod != "GET" || url != "/search")
        {
            await WriteJsonAsync(resp, 404, new { error = "Koristiti GET metodu sa /search?q=..." });
            return;
        }

        var query = req.QueryString["q"];
        if (string.IsNullOrWhiteSpace(query))
        {
            await WriteJsonAsync(resp, 400, new { error = "Nedostaje parametar q." });
            return;
        }

        // reaktivno
        Observable.Return(query)
            .ObserveOn(ThreadPoolScheduler.Instance)
            .SelectMany(q => Observable.FromAsync(() => FetchBooksAsync(q)))
            .SelectMany(json => Observable.FromAsync(()  => ProcessBooksAsync(json)))
            .Subscribe(
                async result =>
                {
                    await WriteJsonAsync(resp, 200, result);
                    dynamic r = result;
                    Console.WriteLine($"Pronađeno {r.UkupnoKnjiga} knjiga");
                },
                async ex =>
                {
                    await WriteJsonAsync(resp, 500, new { error = ex.Message });
                    Console.WriteLine($"Exception: {ex.Message}");
                });
    }

    private async Task<string> FetchBooksAsync(string query)
    {
        string url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}";

        //Console.WriteLine($"FetchBooksAsync {url}");

        var res = await http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            throw new Exception($"Google Books API greška: {res.StatusCode}");
        }

        return body;
    }

    private async Task<object> ProcessBooksAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items))
        {
            return new { error = "Nema pronađenih knjiga." };
        }

        var books = new List<object>();

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("volumeInfo", out var info)) continue;

            string title = info.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string desc = info.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc))
            {
                continue;
            }

            int velika = CountVelikaSlova(desc);
            int jedinstvene = CountJedinstveneReci(desc);

            books.Add(new
            {
                Naslov = title,
                Opis = desc,
                VelikaSlova = velika,
                JedinstveneReci = jedinstvene
            });
        }

        var sorted = books
            .OrderByDescending(b => ((dynamic)b).VelikaSlova)
            .ThenByDescending(b => ((dynamic)b).JedinstveneReci)
            .ToList();

        await Task.Yield();

        return new
        {
            UkupnoKnjiga = sorted.Count,
            Rezultati = sorted
        };
    }

    private int CountVelikaSlova(string text)
    {
        var words = text.Split(new[] { ' ', '\n', '\r', '\t', '\\', '-', '\"',  '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Count(w => char.IsUpper(w[0]));
    }

    private int CountJedinstveneReci(string text)
    {
        var words = text
            .ToLower()
            .Split(new[] { ' ', '\n', '\r', '\t', '\\', '-', '\"', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Distinct().Count();
    }

    private async Task WriteJsonAsync(HttpListenerResponse resp, int code, object obj)
    {
        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        byte[] data = Encoding.UTF8.GetBytes(json);

        resp.StatusCode = code;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = data.Length;

        await resp.OutputStream.WriteAsync(data, 0, data.Length);
        resp.OutputStream.Close();
    }
}