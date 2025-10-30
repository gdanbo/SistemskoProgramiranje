class Program
{
    public static void Main()
    {
        var server = new ReactiveWebServer("http://localhost:5050/");
        server.Start();

        Console.WriteLine("Server pokrenut na http://localhost:5050/");

        Console.WriteLine("\nPritisnite Enter za zaustavljanje servera");
        Console.ReadLine();

        Console.WriteLine("Server se zaustavlja...");
        server.Stop();
    }
}