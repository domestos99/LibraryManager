using System.Collections.Generic;
using Microsoft.Web.LibraryManager.Contracts;

namespace Microsoft.Web.LibraryManager.Providers.json
{
    public class JsonLibrary : ILibrary
    {
        public string Name { get; set; }
        public string ProviderId { get; set; }
        public string Version => "1.0";
        public IReadOnlyDictionary<string, bool> Files { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
