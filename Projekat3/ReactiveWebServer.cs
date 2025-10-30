using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class ReactiveWebServer
{
    private readonly HttpListener listener = new();
    private readonly HttpClient http = new();

    public ReactiveWebServer(string prefix)
    {
        listener.Prefixes.Add(prefix);
    }

    public void Start()
    {
        listener.Start();

        Observable
            .Defer(() => Observable.FromAsync(listener.GetContextAsync))
            .Repeat() // kad se ovaj observable zavrsi, tj. umesto OnCompleted, odmah ga ponovo pokrecemo
            .ObserveOn(ThreadPoolScheduler.Instance) // obradu vrsimo na thread poolu
            .SelectMany(ctx => HandleRequestReactive(ctx))
            .Subscribe(
                (nothing) => { }, // OnNext
                (ex) => Console.WriteLine($"Exception: {ex.Message}"), // OnError
                () => Console.WriteLine("Server pipeline zavrsen") // OnCompleted // nikad se ne desi zbog .Repeat()
            );
    }

    public void Stop()
    {
        listener.Stop();
        listener.Close();
        Console.WriteLine("Server zaustavljen.");
    }

    private IObservable<Unit> HandleRequestReactive(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var url = req.Url?.AbsolutePath ?? "";

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {req.HttpMethod} {req.Url}");

        if (req.HttpMethod != "GET" || url != "/search")
        {
            return Observable.FromAsync(() => WriteJsonAsync(resp, 404, new { error = "Koristiti GET metodu sa /search?q=..." }));
        }

        var query = req.QueryString["q"];
        if (string.IsNullOrWhiteSpace(query))
        {
            return Observable.FromAsync(() => WriteJsonAsync(resp, 400, new { error = "Nedostaje parametar q." }));
        }

        // pipeline za obradu
        return Observable.Return(query)
            .SelectMany(q => Observable.FromAsync(() => FetchBooksAsync(q)))      // poziv Google Books API
            .SelectMany(json => Observable.FromAsync(() => ProcessBooksAsync(json))) // obrada
            .SelectMany(result => Observable.FromAsync(() => WriteJsonAsync(resp, 200, result))) // slanje odgovora
            .Catch<Unit, Exception>(ex =>
                Observable.FromAsync(() => WriteJsonAsync(resp, 500, new { error = ex.Message }))
            );
    }

    private async Task<string> FetchBooksAsync(string author)
    {
        string url = $"https://www.googleapis.com/books/v1/volumes?q=inauthor:{Uri.EscapeDataString(author)}";
        var res = await http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new Exception($"Google Books API greška: {res.StatusCode}");

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
                continue;

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

        return new { UkupnoKnjiga = sorted.Count, Rezultati = sorted };
    }

    private int CountVelikaSlova(string text)
    {
        var words = text.Split(new[] { ' ', '\n', '\r', '\t', '\\', '-', '\"', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Count(w => char.IsUpper(w[0]));
    }

    private int CountJedinstveneReci(string text)
    {
        var words = text
            .ToLower()
            .Split(new[] { ' ', '\n', '\r', '\t', '\\', '-', '\"', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Distinct().Count();
    }

    private async Task<Unit> WriteJsonAsync(HttpListenerResponse resp, int code, object obj)
    {
        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        byte[] data = Encoding.UTF8.GetBytes(json);

        resp.StatusCode = code;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = data.Length;

        await resp.OutputStream.WriteAsync(data, 0, data.Length);
        resp.OutputStream.Close();

        return Unit.Default;
    }
}
