using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;

namespace Microsoft.Web.LibraryManager.Providers.json
{
    public class JsonLibraryGroup : ILibraryGroup
    {
        public JsonLibraryGroup(string groupName)
        {
            DisplayName = groupName;
        }

        public string DisplayName { get; }

        public string Description => string.Empty;

        public Task<IEnumerable<string>> GetLibraryVersions(CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<string>>(Enumerable.Empty<string>());
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
