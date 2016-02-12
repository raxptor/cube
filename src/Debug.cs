using System;

namespace Cube
{
    public static class Debug
    {
        public static void NodeLog(string Msg)
        {
            Console.WriteLine("NODE: " + Msg);
        }

        public static void MasterLog(string Msg)
        {
            Console.WriteLine("MASTER: " + Msg);
        }
    }
}

