using LastTime.Api.Data;
using LastTime.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class UserItemModelTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private LastTimeDbContext NewDb() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<LastTimeDbContext>();

    [Fact]
    public async Task Item_BelongsToUser_AndUserExposesItems()
    {
        var db = NewDb();
        var user = new User { Email = "a@example.com", PasswordHash = "hash" };
        user.Items.Add(new Item { Name = "AC" });
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var loaded = await NewDb().Items.Include(i => i.User).SingleAsync();
        Assert.Equal(user.Id, loaded.UserId);
        Assert.Equal("a@example.com", loaded.User.Email);
        Assert.NotEqual(default, loaded.User.CreatedAt);
    }

    [Fact]
    public async Task Item_WithoutExistingUser_IsRejected()
    {
        var db = NewDb();
        db.Items.Add(new Item { Name = "Orphan", UserId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingUser_WithItems_IsRestricted()
    {
        var db = NewDb();
        var user = new User { Email = "b@example.com", PasswordHash = "hash" };
        user.Items.Add(new Item { Name = "Laptop" });
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var other = NewDb();
        other.Users.Remove(await other.Users.SingleAsync());
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());

        Assert.Single(await NewDb().Items.ToListAsync());
    }

    [Fact]
    public async Task User_IsActiveByDefault()
    {
        var db = NewDb();
        db.Users.Add(new User { Email = "d@example.com", PasswordHash = "hash" });
        await db.SaveChangesAsync();

        Assert.True((await NewDb().Users.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Email_MustBeUnique()
    {
        var db = NewDb();
        db.Users.Add(new User { Email = "c@example.com", PasswordHash = "hash" });
        await db.SaveChangesAsync();

        var other = NewDb();
        other.Users.Add(new User { Email = "c@example.com", PasswordHash = "hash" });
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }
}
