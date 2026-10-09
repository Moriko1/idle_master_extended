using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended.Tests
{
    internal static class PrivateQueueTests
    {
        private const string Profile = "https://steamcommunity.com/profiles/76561198000000001";

        public static async Task RunAllAsync()
        {
            await PrivacyFailuresNeverPublishAsync();
            await LaterBadgeFailureKeepsPreviousSnapshotAsync();
            await VerifiedPrivateRowsSkipDetailsAsync();
            await AllPrivateAndEmptyAccountsAreRecognizedAsync();
            await WhitelistUsesVerifiedPrivacyAndCopiesInputsAsync();
            await OrdinaryBlankRowsCannotImplyPrivacyAsync();
            await CancellationNeverPublishesAsync();
        }

        private static async Task PrivacyFailuresNeverPublishAsync()
        {
            var failures = new[]
            {
                SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.TransientFailure, "Synthetic privacy timeout"),
                SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.TransientFailure, "Synthetic HTTP 429"),
                SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.LoginRequired, "Synthetic expired session"),
                SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.MalformedPage, "Synthetic unknown privacy format")
            };
            foreach (var failure in failures)
            foreach (var whitelist in new IEnumerable<string>[] { null, new[] { "10" } })
            {
                var client = new FakeCommunity();
                var privateReader = new FakePrivate(failure);
                var owned = new FakeOwned();
                var callbacks = 0;
                var result = await new GameQueueScanner(client, owned, privateReader)
                    .ReadAsync(Profile, whitelist, CancellationToken.None, ids => { callbacks++; return Task.CompletedTask; });
                Test.Assert(callbacks == 0, "Failed privacy proof must never invoke the exclusion callback.");
                Test.Assert(result.Status == failure.Status && result.Message == failure.Message && result.Value == null,
                    "Privacy errors must retain their classification and never publish a card or whitelist queue.");
                Test.Assert(privateReader.Calls == 1 && client.Urls.Count == 0 && owned.Calls == 0,
                    "An unverified private-game list must stop before any badge reads.");
            }
            foreach (var proof in new[] { null, new HashSet<int> { 0 }, new HashSet<int> { -1 } })
            {
                var reader = new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(proof));
                var result = await new GameQueueScanner(new FakeCommunity(), new FakeOwned(), reader)
                    .ReadAsync(Profile, new[] { "10" }, CancellationToken.None);
                Test.Assert(result.Status == SteamReadStatus.MalformedPage && result.Value == null,
                    "Missing or invalid private-list proof cannot establish an empty privacy list.");
            }
        }

        private static async Task LaterBadgeFailureKeepsPreviousSnapshotAsync()
        {
            var firstPrivate = new HashSet<int> { 20 };
            var initialClient = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(Row(10, "3 card drops remaining"))));
            var initial = await new GameQueueScanner(initialClient, new FakeOwned(),
                new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(firstPrivate)))
                .ReadAsync(Profile, null, CancellationToken.None);
            Test.Assert(initial.IsSuccess, "The initial verified snapshot should succeed.");
            var secondPrivate = new HashSet<int> { 30 };
            var pageOne = Page(Row(10, "1 card drop remaining"))
                .Replace("</body>", "<a class='pagelink' href='?p=2'>2</a></body>");
            var failedClient = new FakeCommunity(SteamReadResult<string>.Succeeded(pageOne),
                SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Synthetic second-page HTTP 429"));
            HashSet<int> actionablePrivacy = null;
            var failed = await new GameQueueScanner(failedClient, new FakeOwned(),
                new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(secondPrivate)))
                .ReadAsync(Profile, null, CancellationToken.None, ids =>
                {
                    Test.Assert(failedClient.Urls.Count == 0, "Private exclusions must be applied before badge requests.");
                    actionablePrivacy = ids;
                    return Task.CompletedTask;
                });
            Test.Assert(actionablePrivacy != null && actionablePrivacy.SetEquals(new[] { 30 }),
                "Verified positive privacy proof must remain actionable despite a later card-page failure.");
            Test.Assert(failed.Status == SteamReadStatus.TransientFailure && failed.Value == null,
                "A later-page failure must not expose new privacy metadata or a partial queue.");
            Test.Assert(initial.Value.Badges.Single().RemainingCard == 3
                && initial.Value.PrivateAppIds.SetEquals(new[] { 20 }),
                "An older snapshot remains immutable while new positive privacy proof is applied separately.");
            Test.Assert(firstPrivate.SetEquals(new[] { 20 }) && secondPrivate.SetEquals(new[] { 30 }),
                "Neither successful nor failed scans may mutate reader-owned private sets.");
        }

        private static async Task VerifiedPrivateRowsSkipDetailsAsync()
        {
            var privacy = new HashSet<int> { 20 };
            var client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(Row(10, "3 card drops remaining"), EmptyRow(20))));
            var owned = new FakeOwned();
            var result = await new GameQueueScanner(client, owned,
                new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(privacy)))
                .ReadAsync(Profile, null, CancellationToken.None);
            Test.Assert(result.IsSuccess && result.Value.Badges.Count == 2,
                "A verified private badge must not invalidate an otherwise readable card queue.");
            Test.Assert(!result.Value.Badges.Single(badge => badge.AppId == 10).IsPrivate
                && result.Value.Badges.Single(badge => badge.AppId == 10).RemainingCard == 3
                && result.Value.Badges.Single(badge => badge.AppId == 20).IsPrivate,
                "Private flags must come from the verified list while ordinary counts remain accurate.");
            Test.Assert(client.Urls.Count == 1 && owned.Calls == 0,
                "Known private rows must never cause card-detail or ownership fallback requests.");
            Test.Assert(privacy.SetEquals(new[] { 20 }), "Badge scanning must not edit its private-list input.");
        }

        private static async Task AllPrivateAndEmptyAccountsAreRecognizedAsync()
        {
            foreach (var rows in new[] { new[] { EmptyRow(10), EmptyRow(20) }, new string[0] })
            {
                var client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(rows)));
                var result = await new GameQueueScanner(client, new FakeOwned(),
                    new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10, 20 })))
                    .ReadAsync(Profile, null, CancellationToken.None);
                Test.Assert(result.IsSuccess && result.Value.Badges.Count == rows.Length
                    && result.Value.Badges.All(badge => badge.IsPrivate),
                    "Recognized empty or entirely private badge sheets are valid snapshots.");
                Test.Assert(client.Urls.Count == 1 && result.Value.PrivateAppIds.SetEquals(new[] { 10, 20 }),
                    "Even an empty queue needs explicit privacy proof and no private detail requests.");
            }
        }

        private static async Task WhitelistUsesVerifiedPrivacyAndCopiesInputsAsync()
        {
            var inputPrivate = new HashSet<int> { 20 };
            var whitelist = new List<string> { "20", "10", "20", "0", "-1", "bad", "2147483648" };
            var originalWhitelist = whitelist.ToArray();
            var client = new FakeCommunity();
            var reader = new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(inputPrivate));
            reader.BeforeReturn = () => whitelist.Add("30");
            var owned = new FakeOwned();
            var result = await new GameQueueScanner(client, owned, reader)
                .ReadAsync(Profile, whitelist, CancellationToken.None, ids =>
                {
                    ids.Add(70);
                    return Task.CompletedTask;
                });
            Test.Assert(result.IsSuccess && result.Value.Badges.Count == 2
                && result.Value.Badges.All(badge => badge.RemainingCard == -1)
                && result.Value.Badges.Single(badge => badge.AppId == 20).IsPrivate
                && !result.Value.Badges.Single(badge => badge.AppId == 10).IsPrivate,
                "Whitelist queues contain unique positive IDs with flags from explicit private proof.");
            Test.Assert(!inputPrivate.Contains(70) && !result.Value.PrivateAppIds.Contains(70),
                "The exclusion callback must receive an independent copy of verified private IDs.");
            Test.Assert(client.Urls.Count == 0 && owned.Calls == 0 && reader.Calls == 1,
                "A verified whitelist does not need card or ownership reads.");
            Test.Assert(whitelist.Take(originalWhitelist.Length).SequenceEqual(originalWhitelist)
                && whitelist.Last() == "30" && !result.Value.Badges.Any(badge => badge.AppId == 30),
                "An asynchronous input edit must not change the captured whitelist or be undone by scanning.");
            inputPrivate.Add(40);
            Test.Assert(!result.Value.PrivateAppIds.Contains(40),
                "A returned snapshot must own a copy of the reader's private-game set.");
            result.Value.PrivateAppIds.Add(50);
            Test.Assert(!inputPrivate.Contains(50), "Editing snapshot metadata cannot edit reader-owned proof.");
            whitelist.Clear();
            Test.Assert(result.Value.Badges.Count == 2, "Editing the whitelist input cannot edit the published queue.");
        }

        private static async Task OrdinaryBlankRowsCannotImplyPrivacyAsync()
        {
            var detail = Page(EmptyRow(10).Replace("badge_row is_link", "badge_row badge_gamecard_page"));
            var client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyRow(10))),
                SteamReadResult<string>.Succeeded(detail));
            var owned = new FakeOwned(new HashSet<int> { 10 });
            var result = await new GameQueueScanner(client, owned,
                new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int>())))
                .ReadAsync(Profile, null, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.MalformedPage && result.Value == null,
                "A normal owned game's blank card status must never be guessed to mean private or zero cards.");
            Test.Assert(client.Urls.Count == 2 && owned.Calls == 1,
                "Unknown ordinary counts still require the existing explicit detail and ownership checks.");
        }

        private static async Task CancellationNeverPublishesAsync()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var reader = new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int>()));
                var scanner = new GameQueueScanner(new FakeCommunity(), new FakeOwned(), reader);
                await ExpectCancellationAsync(() => scanner.ReadAsync(Profile, null, cancellation.Token));
                Test.Assert(reader.Calls == 0, "Pre-canceled reads must not issue privacy requests.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var reader = new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int>()));
                reader.BeforeReturn = cancellation.Cancel;
                var client = new FakeCommunity();
                var scanner = new GameQueueScanner(client, new FakeOwned(), reader);
                await ExpectCancellationAsync(() => scanner.ReadAsync(Profile, new[] { "10" }, cancellation.Token));
                Test.Assert(client.Urls.Count == 0, "Cancel after privacy proof must not publish a whitelist.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(Row(10, "3 card drops remaining"))));
                client.BeforeReturn = cancellation.Cancel;
                var scanner = new GameQueueScanner(client, new FakeOwned(),
                    new FakePrivate(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int>())));
                await ExpectCancellationAsync(() => scanner.ReadAsync(Profile, null, cancellation.Token));
            }
        }

        private static async Task ExpectCancellationAsync(Func<Task<SteamReadResult<GameQueueSnapshot>>> action)
        {
            var canceled = false;
            try { await action(); }
            catch (OperationCanceledException) { canceled = true; }
            Test.Assert(canceled, "Cancellation must propagate without a failure or successful snapshot.");
        }

        private static string Page(params string[] rows)
        {
            return "<html><body><div class='badges_sheet'>" + string.Join("", rows) + "</div></body></html>";
        }

        private static string Row(int appId, string drops)
        {
            return "<div class='badge_row is_link'><a class='badge_row_overlay' href='/gamecards/" + appId
                + "/'></a><div class='badge_title'>Synthetic game " + appId
                + "</div><div class='badge_title_stats_playtime'>2.2 hrs on record</div>"
                + "<div class='badge_title_stats_drops'><span class='progress_info_bold'>" + drops
                + "</span></div></div>";
        }

        private static string EmptyRow(int appId)
        {
            return Row(appId, "").Replace("<span class='progress_info_bold'></span>", "  ");
        }

        private sealed class FakePrivate : IPrivateGamesReader
        {
            private readonly SteamReadResult<HashSet<int>> result;
            public int Calls { get; private set; }
            public Action BeforeReturn;
            public FakePrivate(SteamReadResult<HashSet<int>> result) { this.result = result; }
            public Task<SteamReadResult<HashSet<int>>> ReadPrivateAsync(string profileUrl, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Test.Assert(profileUrl == Profile, "Privacy requests must use the same authenticated profile.");
                Calls++;
                BeforeReturn?.Invoke();
                return Task.FromResult(result);
            }
        }

        private sealed class FakeOwned : IOwnedGamesReader
        {
            private readonly HashSet<int> apps;
            public int Calls { get; private set; }
            public FakeOwned(HashSet<int> apps = null) { this.apps = apps ?? new HashSet<int>(); }
            public Task<SteamReadResult<HashSet<int>>> ReadAsync(string profileUrl, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Calls++;
                return Task.FromResult(SteamReadResult<HashSet<int>>.Succeeded(apps));
            }
        }

        private sealed class FakeCommunity : ICommunityClient
        {
            private readonly Queue<SteamReadResult<string>> responses;
            public List<string> Urls { get; } = new List<string>();
            public Action BeforeReturn;
            public FakeCommunity(params SteamReadResult<string>[] responses)
            {
                this.responses = new Queue<SteamReadResult<string>>(responses);
            }
            public Task<SteamReadResult<string>> GetAsync(string url, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Urls.Add(url);
                Test.Assert(responses.Count > 0, "Unexpected private-card detail or badge request: " + url);
                BeforeReturn?.Invoke();
                return Task.FromResult(responses.Dequeue());
            }
        }
    }
}
