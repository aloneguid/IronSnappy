using System.IO;
using Xunit;

namespace IronSnappy.Test
{
   public class RoundtripTest
   {
      [Fact]
      public void RoundtripGolden()
      {
         string text = File.ReadAllText("TestData/Mark.Twain-Tom.Sawyer.txt");

         using (MemoryStream memStream = new MemoryStream())
         {
            using(Stream zip = Snappy.OpenWriter(memStream))
            using(StreamWriter writer = new StreamWriter(zip))
            {
               writer.Write(text);
            }

            memStream.Seek(0, SeekOrigin.Begin);

            using(Stream zip = Snappy.OpenReader(memStream))
            using(StreamReader reader = new StreamReader(zip))
            {
               string result = reader.ReadToEnd();
               Assert.Equal(text, result);
            }
         }
      }

      [Fact]
      public void RoundtripBinary()
      {
         byte[] originalBytes = new byte[] { 1, 2, 3, 4, 5, 6 };

         using(MemoryStream memStream = new MemoryStream())
         {
            using(Stream zip = Snappy.OpenWriter(memStream))
            {
               zip.Write(originalBytes);
            }

            memStream.Seek(0, SeekOrigin.Begin);

            using(Stream zip = Snappy.OpenReader(memStream))
            {
               byte[] buffer = new byte[1024];
               int n = zip.Read(buffer);
               Assert.Equal(n, originalBytes.Length);
               for (int i = 0; i < n; i++)
               {
                  Assert.Equal(originalBytes[i], buffer[i]);
               }
            }
         }
      }

   }
}
