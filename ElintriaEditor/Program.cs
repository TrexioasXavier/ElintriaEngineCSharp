using System;
using System.Diagnostics;

class Program
{
    static void Main(string[] args)
    {
        using (Elintria.ElintriaEditor editor = new Elintria.ElintriaEditor())
        {
            editor.Run();
        } 
    }
}