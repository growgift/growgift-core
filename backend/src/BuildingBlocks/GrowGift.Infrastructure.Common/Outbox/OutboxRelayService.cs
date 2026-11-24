using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrowGift.Infrastructure.Common.Outbox;

/// <summary>
/// Polls unpublished outbox rows and publishes them to Kafka with exponential
/// backoff. Runs per-service. Publication order is preserved per partition key.
/// </summary>
public sealed class OutboxRelayService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IProducer<string, string> producer,
    ILogger<OutboxRelayService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 100;
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay batch failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RelayBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.PublishedAtUtc == null && m.AttemptCount < MaxAttempts)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            try
            {
                await producer.ProduceAsync(message.Topic, new Message<string, string>
                {
                    Key = message.PartitionKey ?? message.Id.ToString(),
                    Value = message.Payload,
                    Headers = new Headers
                    {
                        { "event-type", System.Text.Encoding.UTF8.GetBytes(message.Type) },
                        { "event-id", System.Text.Encoding.UTF8.GetBytes(message.Id.ToString()) }
                    }
                }, ct);

                message.PublishedAtUtc = DateTimeOffset.UtcNow;
            }
            catch (ProduceException<string, string> ex)
            {
                message.AttemptCount++;
                message.LastError = ex.Error.Reason;
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId}, attempt {Attempt}",
                    message.Id, message.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
