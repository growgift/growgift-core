using GrowGift.Fulfillment.Shipping;
using Xunit;

namespace GrowGift.Fulfillment.Tests;

public class GuardAndMappingTests
{
    private static ShippitOptions Opt(string url = ShippitOptions.DefaultBaseUrl, string env = "staging") => new() { BaseUrl = url, Environment = env };

    [Fact] public void Staging_url_is_accepted() { ShippitGuard.EnsureStaging(Opt()); }
    [Fact] public void Base_address_ends_with_slash() => Assert.Equal("https://app.staging.shippit.com/api/3/", ShippitGuard.BaseAddress(Opt()).ToString());

    [Theory]
    [InlineData("https://app.shippit.com/api/3")]               // production host
    [InlineData("https://www.shippit.com/api/3")]
    [InlineData("http://app.staging.shippit.com/api/3")]        // not https
    [InlineData("https://app.staging.shippit.com.evil.test/api/3")]
    [InlineData("https://user:pw@app.staging.shippit.com/api/3")]
    [InlineData("https://app.staging.shippit.com/api/3?x=1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Anything_but_staging_https_is_refused(string url)
        => Assert.True(Throws(() => ShippitGuard.EnsureStaging(Opt(url))));

    [Theory]
    [InlineData("production")]
    [InlineData("prod")]
    [InlineData("")]
    [InlineData("live")]
    public void Non_staging_environment_is_refused(string env)
        => Assert.True(Throws(() => ShippitGuard.EnsureStaging(Opt(env: env))));

    [Fact] public void Production_cannot_be_reached_by_configuring_the_http_client()
    {
        var o = Opt("https://app.shippit.com/api/3"); o.ApiSecret = "x";
        Assert.True(Throws(() => ShippitClient.Configure(new HttpClient(), o)));
    }

    [Fact] public void Staging_client_gets_bearer_header_only_when_configured()
    {
        var c = new HttpClient(); var o = Opt(); o.ApiSecret = "abc";
        ShippitClient.Configure(c, o);
        Assert.Equal("Bearer", c.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("app.staging.shippit.com", c.BaseAddress!.Host);
        var c2 = new HttpClient(); ShippitClient.Configure(c2, Opt());
        Assert.Null(c2.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("https://hooks.growgift.example/api/v1/shipping/webhooks/shippit", true)]
    [InlineData("http://hooks.growgift.example/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://u:p@hooks.example/x", false)]
    [InlineData("", false)]
    public void Webhook_url_must_be_public_https(string url, bool ok) => Assert.Equal(ok, ShippitGuard.IsAcceptableWebhookUrl(url));

    // ---- address ----
    private static ShippitAddress Addr(string? state = "NSW", string? pc = "2000", string country = "AU", string name = "A B", string line = "1 St", string city = "Sydney", string email = "a@b.c")
        => new(name, line, null, city, state!, pc!, country, null, email);

    [Fact] public void Good_au_address_passes() => Assert.True(AddressValidator.Check(Addr()).Ok);
    [Theory]
    [InlineData("XX", "2000", "AU", "address.state_invalid")]
    [InlineData("NSW", "20", "AU", "address.postcode_invalid")]
    [InlineData("NSW", "2000", "NZ", "address.country_unsupported")]
    public void Bad_address_gets_a_code(string state, string pc, string country, string code)
    {
        var r = AddressValidator.Check(Addr(state, pc, country));
        Assert.False(r.Ok); Assert.Equal(code, r.Code);
    }
    [Fact] public void Address_codes_never_echo_the_address()
    {
        var r = AddressValidator.Check(Addr(line: "", name: "Secret Person"));
        Assert.DoesNotContain("Secret", r.Code);
    }
    [Fact] public void Name_is_split() { Assert.Equal(("Ann", (string?)"Lee Smith"), AddressValidator.SplitName("Ann Lee Smith")); Assert.Equal(("Ann", (string?)null), AddressValidator.SplitName("Ann")); }

    // ---- state map ----
    [Theory]
    [InlineData("order_placed", ShipmentStatus.Booked)]
    [InlineData("in_transit", ShipmentStatus.InTransit)]
    [InlineData("with_driver", ShipmentStatus.InTransit)]
    [InlineData("completed", ShipmentStatus.Delivered)]
    [InlineData("cancelled", ShipmentStatus.Cancelled)]
    [InlineData("delivery_failed", ShipmentStatus.Exception)]
    [InlineData("In Transit", ShipmentStatus.InTransit)]
    public void States_map(string s, ShipmentStatus expected) => Assert.Equal(expected, ShippitStateMap.Map(s));

    [Fact] public void Unknown_state_maps_to_nothing() => Assert.Null(ShippitStateMap.Map("something_new"));

    [Fact] public void Late_ready_for_pickup_cannot_move_a_shipment_backwards()
        => Assert.Null(ShippitStateMap.Apply(ShipmentStatus.InTransit, ShipmentStatus.Booked));
    [Fact] public void Terminal_states_are_final()
    {
        Assert.Null(ShippitStateMap.Apply(ShipmentStatus.Delivered, ShipmentStatus.InTransit));
        Assert.Null(ShippitStateMap.Apply(ShipmentStatus.Delivered, ShipmentStatus.Exception));
        Assert.Null(ShippitStateMap.Apply(ShipmentStatus.Cancelled, ShipmentStatus.Delivered));
    }
    [Fact] public void Exception_can_recover()
    {
        Assert.Equal(ShipmentStatus.InTransit, ShippitStateMap.Apply(ShipmentStatus.Exception, ShipmentStatus.InTransit));
        Assert.Equal(ShipmentStatus.Delivered, ShippitStateMap.Apply(ShipmentStatus.Exception, ShipmentStatus.Delivered));
        Assert.Null(ShippitStateMap.Apply(ShipmentStatus.Exception, ShipmentStatus.Booked));
    }
    [Fact] public void Webhook_can_skip_ahead_when_we_missed_steps()
        => Assert.Equal(ShipmentStatus.InTransit, ShippitStateMap.Apply(ShipmentStatus.Created, ShipmentStatus.InTransit));

    private static bool Throws(Action a) { try { a(); return false; } catch (ShippitConfigurationException) { return true; } }
}
