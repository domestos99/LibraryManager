using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;
using Newtonsoft.Json;

namespace Microsoft.Web.LibraryManager.Providers.json
{
    public class JsonLibraryGroup : ILibraryGroup
    {
        [JsonProperty("name")]
        public string DisplayName { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("files")]
        public List<string> Files { get; set; }

        public Task<IEnumerable<string>> GetLibraryVersions(CancellationToken cancellationToken)
        {
            return DisplayInfosTask?.Invoke(cancellationToken) ?? Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        }

        public Func<CancellationToken, Task<IEnumerable<string>>> DisplayInfosTask { get; set; }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
