class Program
{
    static async Task Main()
    {
        var server = new WebServer("http://localhost:5050/");
        await server.StartAsync();

        Console.WriteLine("Server pokrenut na http://localhost:5050/");
        await Task.Delay(-1); // beskonacno
    }

}