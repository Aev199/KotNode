using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KotNode.Core
{
    // Order-independent integrity stamp of a generated entity inventory.
    // Length-prefix encoding avoids separator collisions; never used as a persistent edge identity.
    public static class ViewGeometryStamp
    {
        public static string Record(params string[] fields)
        {
            if(fields==null)throw new ArgumentNullException(nameof(fields));
            var b=new StringBuilder();
            foreach(var field in fields)
            {
                if(field==null)b.Append("-1:");
                else b.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
            }
            return b.ToString();
        }
        public static string Compute(IEnumerable<string> records)
        {
            if(records==null)throw new ArgumentNullException(nameof(records));
            var payload=Record(records.OrderBy(x=>x,StringComparer.Ordinal).ToArray());
            using(var sha=SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }
    }
}
