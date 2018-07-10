﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Redis.Tests.Booksleeve
{
    public class Scripting : BookSleeveTestBase
    {
        public Scripting(ITestOutputHelper output) : base(output) { }

        private static ConnectionMultiplexer GetScriptConn(bool allowAdmin = false)
        {
            int syncTimeout = 5000;
            if (Debugger.IsAttached) syncTimeout = 500000;
            var muxer = GetUnsecuredConnection(waitForOpen: true, allowAdmin: allowAdmin, syncTimeout: syncTimeout);

            Skip.IfMissingFeature(muxer, nameof(RedisFeatures.Scripting), r => r.Scripting);
            return muxer;
        }

        [Fact]
        public void ClientScripting()
        {
            using (var conn = GetScriptConn())
            {
                var result = conn.GetDatabase().ScriptEvaluate("return redis.call('info','server')", null, null);
            }
        }

        [Fact]
        public void BasicScripting()
        {
            using (var muxer = GetScriptConn())
            {
                var conn = muxer.GetDatabase();
                var noCache = conn.ScriptEvaluateAsync("return {KEYS[1],KEYS[2],ARGV[1],ARGV[2]}",
                    new RedisKey[] { "key1", "key2" }, new RedisValue[] { "first", "second" });
                var cache = conn.ScriptEvaluateAsync("return {KEYS[1],KEYS[2],ARGV[1],ARGV[2]}",
                    new RedisKey[] { "key1", "key2" }, new RedisValue[] { "first", "second" });
                var results = (string[])conn.Wait(noCache);
                Assert.Equal(4, results.Length);
                Assert.Equal("key1", results[0]);
                Assert.Equal("key2", results[1]);
                Assert.Equal("first", results[2]);
                Assert.Equal("second", results[3]);

                results = (string[])conn.Wait(cache);
                Assert.Equal(4, results.Length);
                Assert.Equal("key1", results[0]);
                Assert.Equal("key2", results[1]);
                Assert.Equal("first", results[2]);
                Assert.Equal("second", results[3]);
            }
        }

        [Fact]
        public void KeysScripting()
        {
            using (var muxer = GetScriptConn())
            {
                var conn = muxer.GetDatabase();
                var key = Me();
                conn.StringSet(key, "bar");
                var result = (string)conn.ScriptEvaluate("return redis.call('get', KEYS[1])", new RedisKey[] { key }, null);
                Assert.Equal("bar", result);
            }
        }

        [Fact]
        public void TestRandomThingFromForum()
        {
            const string script = @"local currentVal = tonumber(redis.call('GET', KEYS[1]));
                if (currentVal <= 0 ) then return 1 elseif (currentVal - (tonumber(ARGV[1])) < 0 ) then return 0 end;
                return redis.call('INCRBY', KEYS[1], -tonumber(ARGV[1]));";

            using (var muxer = GetScriptConn())
            {
                var prefix = Me();
                var conn = muxer.GetDatabase();
                conn.StringSetAsync(prefix + "A", "0");
                conn.StringSetAsync(prefix + "B", "5");
                conn.StringSetAsync(prefix + "C", "10");

                var a = conn.ScriptEvaluateAsync(script, new RedisKey[] { prefix + "A" }, new RedisValue[] { 6 });
                var b = conn.ScriptEvaluateAsync(script, new RedisKey[] { prefix + "B" }, new RedisValue[] { 6 });
                var c = conn.ScriptEvaluateAsync(script, new RedisKey[] { prefix + "C" }, new RedisValue[] { 6 });

                var vals = conn.StringGetAsync(new RedisKey[] { prefix + "A", prefix + "B", prefix + "C" });

                Assert.Equal(1, (long)conn.Wait(a)); // exit code when current val is non-positive
                Assert.Equal(0, (long)conn.Wait(b)); // exit code when result would be negative
                Assert.Equal(4, (long)conn.Wait(c)); // 10 - 6 = 4
                Assert.Equal("0", conn.Wait(vals)[0]);
                Assert.Equal("5", conn.Wait(vals)[1]);
                Assert.Equal("4", conn.Wait(vals)[2]);
            }
        }

        [Fact]
        public void HackyGetPerf()
        {
            using (var muxer = GetScriptConn())
            {
                var key = Me();
                var conn = muxer.GetDatabase();
                conn.StringSetAsync(key + "foo", "bar");
                var result = (long)conn.ScriptEvaluate(@"
redis.call('psetex', KEYS[1], 60000, 'timing')
for i = 1,100000 do
    redis.call('set', 'ignore','abc')
end
local timeTaken = 60000 - redis.call('pttl', KEYS[1])
redis.call('del', KEYS[1])
return timeTaken
", new RedisKey[] { key }, null);
                Output.WriteLine(result.ToString());
                Assert.True(result > 0);
            }
        }

        [Fact]
        public void MultiIncrWithoutReplies()
        {
            using (var muxer = GetScriptConn())
            {
                var conn = muxer.GetDatabase();
                var prefix = Me();
                // prime some initial values
                conn.KeyDeleteAsync(new RedisKey[] { prefix + "a", prefix + "b", prefix + "c" });
                conn.StringIncrementAsync(prefix + "b");
                conn.StringIncrementAsync(prefix + "c");
                conn.StringIncrementAsync(prefix + "c");

                // run the script, passing "a", "b", "c", "c" to
                // increment a & b by 1, c twice
                var result = conn.ScriptEvaluateAsync(
                    "for i,key in ipairs(KEYS) do redis.call('incr', key) end",
                    new RedisKey[] { prefix + "a", prefix + "b", prefix + "c", prefix + "c" }, // <== aka "KEYS" in the script
                    null); // <== aka "ARGV" in the script

                // check the incremented values
                var a = conn.StringGetAsync(prefix + "a");
                var b = conn.StringGetAsync(prefix + "b");
                var c = conn.StringGetAsync(prefix + "c");

                Assert.True(conn.Wait(result).IsNull, "result");
                Assert.Equal(1, (long)conn.Wait(a));
                Assert.Equal(2, (long)conn.Wait(b));
                Assert.Equal(4, (long)conn.Wait(c));
            }
        }

        [Fact]
        public void MultiIncrByWithoutReplies()
        {
            using (var muxer = GetScriptConn())
            {
                var conn = muxer.GetDatabase();
                var prefix = Me();
                // prime some initial values
                conn.KeyDeleteAsync(new RedisKey[] { prefix + "a", prefix + "b", prefix + "c" });
                conn.StringIncrementAsync(prefix + "b");
                conn.StringIncrementAsync(prefix + "c");
                conn.StringIncrementAsync(prefix + "c");

                //run the script, passing "a", "b", "c" and 1,2,3
                // increment a &b by 1, c twice
                var result = conn.ScriptEvaluateAsync(
                    "for i,key in ipairs(KEYS) do redis.call('incrby', key, ARGV[i]) end",
                    new RedisKey[] { prefix + "a", prefix + "b", prefix + "c" }, // <== aka "KEYS" in the script
                    new RedisValue[] { 1, 1, 2 }); // <== aka "ARGV" in the script

                // check the incremented values
                var a = conn.StringGetAsync(prefix + "a");
                var b = conn.StringGetAsync(prefix + "b");
                var c = conn.StringGetAsync(prefix + "c");

                Assert.True(conn.Wait(result).IsNull, "result");
                Assert.Equal(1, (long)conn.Wait(a));
                Assert.Equal(2, (long)conn.Wait(b));
                Assert.Equal(4, (long)conn.Wait(c));
            }
        }

        [Fact]
        public void DisableStringInference()
        {
            using (var muxer = GetScriptConn())
            {
                var conn = muxer.GetDatabase();
                var key = Me();
                conn.StringSet(key, "bar");
                var result = (byte[])conn.ScriptEvaluate("return redis.call('get', KEYS[1])", new RedisKey[] { key });
                Assert.Equal("bar", Encoding.UTF8.GetString(result));
            }
        }

        [Fact]
        public void FlushDetection()
        { // we don't expect this to handle everything; we just expect it to be predictable
            using (var muxer = GetScriptConn(allowAdmin: true))
            {
                var conn = muxer.GetDatabase();
                var key = Me();
                conn.StringSet(key, "bar");
                var result = (string)conn.ScriptEvaluate("return redis.call('get', KEYS[1])", new RedisKey[] { key }, null);
                Assert.Equal("bar", result);

                // now cause all kinds of problems
                GetServer(muxer).ScriptFlush();

                //expect this one to <strike>fail</strike> just work fine (self-fix)
                conn.ScriptEvaluate("return redis.call('get', KEYS[1])", new RedisKey[] { key }, null);

                result = (string)conn.ScriptEvaluate("return redis.call('get', KEYS[1])", new RedisKey[] { key }, null);
                Assert.Equal("bar", result);
            }
        }

        [Fact]
        public void PrepareScript()
        {
            string[] scripts = { "return redis.call('get', KEYS[1])", "return {KEYS[1],KEYS[2],ARGV[1],ARGV[2]}" };
            using (var muxer = GetScriptConn(allowAdmin: true))
            {
                var server = GetServer(muxer);
                server.ScriptFlush();

                // when vanilla
                server.ScriptLoad(scripts[0]);
                server.ScriptLoad(scripts[1]);

                //when known to exist
                server.ScriptLoad(scripts[0]);
                server.ScriptLoad(scripts[1]);
            }
            using (var muxer = GetScriptConn())
            {
                var server = GetServer(muxer);

                //when vanilla
                server.ScriptLoad(scripts[0]);
                server.ScriptLoad(scripts[1]);

                //when known to exist
                server.ScriptLoad(scripts[0]);
                server.ScriptLoad(scripts[1]);

                //when known to exist
                server.ScriptLoad(scripts[0]);
                server.ScriptLoad(scripts[1]);
            }
        }

        [Fact]
        public void NonAsciiScripts()
        {
            using (var muxer = GetScriptConn())
            {
                const string evil = "return '僕'";
                var conn = muxer.GetDatabase();
                GetServer(muxer).ScriptLoad(evil);

                var result = (string)conn.ScriptEvaluate(evil, null, null);
                Assert.Equal("僕", result);
            }
        }

        [Fact]
        public void ScriptThrowsError()
        {
            Assert.Throws<RedisServerException>(() =>
            {
                using (var muxer = GetScriptConn())
                {
                    var conn = muxer.GetDatabase();
                    var result = conn.ScriptEvaluateAsync("return redis.error_reply('oops')", null, null);
                    try
                    {
                        conn.Wait(result);
                    }
                    catch (AggregateException ex)
                    {
                        throw ex.InnerExceptions[0];
                    }
                }
            });
        }

        [Fact]
        public void ScriptThrowsErrorInsideTransaction()
        {
            using (var muxer = GetScriptConn())
            {
                var key = Me();
                var conn = muxer.GetDatabase();
                conn.KeyDeleteAsync(key);
                var beforeTran = (string)conn.StringGet(key);
                Assert.Null(beforeTran);
                var tran = conn.CreateTransaction();
                {
                    var a = tran.StringIncrementAsync(key);
                    var b = tran.ScriptEvaluateAsync("return redis.error_reply('oops')", null, null);
                    var c = tran.StringIncrementAsync(key);
                    var complete = tran.ExecuteAsync();

                    Assert.True(tran.Wait(complete));
                    Assert.True(a.IsCompleted);
                    Assert.True(c.IsCompleted);
                    Assert.Equal(1L, a.Result);
                    Assert.Equal(2L, c.Result);

                    Assert.True(b.IsFaulted);
                    Assert.Single(b.Exception.InnerExceptions);
                    var ex = b.Exception.InnerExceptions.Single();
                    Assert.IsType<RedisServerException>(ex);
                    Assert.Equal("oops", ex.Message);
                }
                var afterTran = conn.StringGetAsync(key);
                Assert.Equal(2L, (long)conn.Wait(afterTran));
            }
        }

        [Fact]
        public void ChangeDbInScript()
        {
            using (var muxer = GetScriptConn())
            {
                var key = Me();
                muxer.GetDatabase(1).StringSet(key, "db 1");
                muxer.GetDatabase(2).StringSet(key, "db 2");

                Output.WriteLine("Key: " + key);
                var conn = muxer.GetDatabase(2);
                var evalResult = conn.ScriptEvaluateAsync(@"redis.call('select', 1)
        return redis.call('get','" + key +"')", null, null);
                var getResult = conn.StringGetAsync(key);

                Assert.Equal("db 1", (string)conn.Wait(evalResult));
                // now, our connection thought it was in db 2, but the script changed to db 1
                Assert.Equal("db 2", conn.Wait(getResult));
            }
        }

        [Fact]
        public void ChangeDbInTranScript()
        {
            using (var muxer = GetScriptConn())
            {
                var key = Me();
                muxer.GetDatabase(1).StringSet(key, "db 1");
                muxer.GetDatabase(2).StringSet(key, "db 2");

                var conn = muxer.GetDatabase(2);
                var tran = conn.CreateTransaction();
                var evalResult = tran.ScriptEvaluateAsync(@"redis.call('select', 1)
        return redis.call('get','" + key + "')", null, null);
                var getResult = tran.StringGetAsync(key);
                Assert.True(tran.Execute());

                Assert.Equal("db 1", (string)conn.Wait(evalResult));
                // now, our connection thought it was in db 2, but the script changed to db 1
                Assert.Equal("db 2", conn.Wait(getResult));
            }
        }
    }
}
