using Commerce.Application;
using Commerce.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Api;

public sealed class DemoOrderWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DemoOrderWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var queryScope = scopes.CreateAsyncScope();
                var db = queryScope.ServiceProvider.GetRequiredService<CommerceDbContext>();
                var due = await db.Orders.AsNoTracking().Where(o => o.NextTransitionAt <= clock.GetUtcNow())
                    .OrderBy(o => o.NextTransitionAt).Select(o => new { o.Id, o.UserId }).Take(100).ToListAsync(stoppingToken);
                foreach (var order in due)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IOrderService>().AdvanceAsync(order.UserId, order.Id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Demo order transition failed; persisted deadlines will be retried."); }
        }
    }
}
