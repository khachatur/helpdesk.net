using System.Data.Common;

namespace Helpdesk.Infrastructure.Data;

public interface IDbConnectionFactory
{
    /// <summary>Returns an open connection. The caller disposes it.</summary>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}
