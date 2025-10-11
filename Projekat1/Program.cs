class Program
{
    static void Main(string[] args)
    {
        WebServer server = new WebServer("http://localhost:5050/");
        server.Start();

        Console.WriteLine("Server pokrenut na http://localhost:5050/");
        Thread.Sleep(-1); // beskonacno
    }
}