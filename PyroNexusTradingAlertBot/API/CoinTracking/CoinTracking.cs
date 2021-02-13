﻿using System;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.IO;
using System.Data;

namespace PyroNexusTradingAlertBot.API
{
    public class CoinTrackingOptions
    {
        public string key { get; set; }
        public string secret { get; set; }
        public HttpClient client { get; set; }
    }

    public class CoinTracking : ICoinTracking
    {
        public class RequestType
        {
            public const string Balance = "getBalance";
            public const string Trades = "getTrades";
            public const string HistoricalSummary = "getHistoricalSummary";
            public const string HistoricalCurrency = "getHistoricalCurrency";
            public const string GroupedBalance = "getGroupedBalance";
            public const string Gains = "getGains";
        }

        public class CoinTrackingUpdateJob
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public List<int> JobIds { get; set; }
        }

        public List<CoinTrackingUpdateJob> CoinTrackingUpdateJobs = new List<CoinTrackingUpdateJob>()
        {
            new CoinTrackingUpdateJob { Name = "Bittrex", Path = "bittrex_api", JobIds = new List<int>() { 25297 } }
        };

        private readonly ILogger _logger;
        private readonly HttpClient client;

        public readonly string apiUrl = "https://cointracking.info/api/v1/";
        public readonly Uri updateJobUrl = new Uri("https://cointracking.info/import/");
        private readonly string apiKey;
        private readonly string apiSecret;

        public CoinTracking(IOptions<CoinTrackingOptions> options, ILogger<CoinTracking> logger)
        {
            client = options.Value.client;
            apiKey = options.Value.key;
            apiSecret = options.Value.secret;
            _logger = logger;
        }

        private FormUrlEncodedContent prepareRequestData(string method, IEnumerable<KeyValuePair<string, string>> data)
        {
            return new FormUrlEncodedContent(
                Enumerable.Concat(
                  data,
                  new[] {
                    new KeyValuePair<string, string>("method", method),
                    new KeyValuePair<string, string>("nonce", new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds().ToString())
                  }
                )
            );
        }

        private async Task signMessage(FormUrlEncodedContent formData)
        {
            HMACSHA512 hmac = new HMACSHA512(Encoding.ASCII.GetBytes(apiSecret));
            byte[] sign = hmac.ComputeHash(await formData.ReadAsByteArrayAsync());

            formData.Headers.Add("Key", apiKey);
            formData.Headers.Add("Sign", BitConverter.ToString(sign).Replace("-", string.Empty).ToLower());
        }

        private async Task checkResult(Stream stream)
        {
            var json = await JsonSerializer.DeserializeAsync<Result>(stream);

            if (json.success == 0)
            {
                if (string.IsNullOrWhiteSpace(json.error))
                {
                    json.error = "UNKNOWN_ERR";                    
                }
                if (string.IsNullOrWhiteSpace(json.error_msg))
                {
                    json.error_msg = "Unknown error: No error message was provided by the server.";
                }
                _logger.LogError(string.Format("{0}: {1}", json.error, json.error_msg));
            }
        }

        private async Task<Dictionary<string, T>> processStream<T>(Stream stream) where T : class
        {
            var json = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(stream);

            var props = typeof(Result).GetProperties();
            List<string> ignoredProps = new List<string>();
            for (int i = 0; i < props.Length; i++)
            {
                ignoredProps.Add(props[i].Name.ToString());
            }

            Dictionary<string, T> result = new Dictionary<string, T>();
            foreach (var item in json)
            {
                if (ignoredProps.Contains(item.Key))
                {
                    continue;
                }
                result.Add(item.Key, JsonSerializer.Deserialize<T>(item.Value.ToString()));
            }
            return result;
        }

        private async Task<Dictionary<string, T>> DoPost<T>(string request, List<KeyValuePair<string, string>> requestParams) where T : class
        {
            FormUrlEncodedContent formData = prepareRequestData(request, requestParams);
            await signMessage(formData);

            HttpResponseMessage response = await client.PostAsync(apiUrl, formData);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            await checkResult(stream);
            stream.Position = 0;
            return await processStream<T>(stream);
        }

        private async Task<Dictionary<string, T>> DoPost<T>(string request) where T : class
        {
            return await DoPost<T>(request, new List<KeyValuePair<string, string>>());
        }

        private async Task runUpdateJobs(List<CoinTrackingUpdateJob> jobs)
        {
            int delay = 60;
            foreach (CoinTrackingUpdateJob job in jobs)
            {
                _logger.LogDebug("Running update jobs for {0}", job.Name);
                await runUpdateJob(job);
                _logger.LogDebug("Update finished for {0}. Delaying {1} seconds before the next job run...", job.Name, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }

        private async Task runUpdateJob(CoinTrackingUpdateJob job)
        {
            var delay = 10;
            foreach (int jobId in job.JobIds)
            {
                _logger.LogDebug("Running update job for {0} with id {1}", job.Name, jobId);
                var jobUpdatePath = string.Format("{0}/check.php?j={1}&check=check", job.Path, jobId);

                var request = new HttpRequestMessage
                {
                    RequestUri = new Uri(updateJobUrl, jobUpdatePath),
                    Method = HttpMethod.Get
                };

                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.152 Safari/537.36");

                var res = await client.SendAsync(request);
                _logger.LogDebug("Update finished. Delaying {0} seconds before the next job id run...", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }

        public async Task UpdateTrades()
        {
            _logger.LogInformation("Running update jobs...");

            await Task.Run(() => runUpdateJobs(CoinTrackingUpdateJobs));
        }

        public async Task<Dictionary<string, Trade>> GetTrades(int limit = 0, string order = "ASC", int start = 0, int end = 0, bool tradePrices = false)
        {
            List<KeyValuePair<string, string>> optionalParams = new List<KeyValuePair<string, string>>();

            if (limit > 0)
            {
                optionalParams.Add(new KeyValuePair<string, string>("limit", limit.ToString()));
            }

            optionalParams.Add(new KeyValuePair<string, string>("order", order));

            if (start > 0)
            {
                optionalParams.Add(new KeyValuePair<string, string>("start", start.ToString()));
            }

            if (end > 0)
            {
                optionalParams.Add(new KeyValuePair<string, string>("end", end.ToString()));
            }

            if (tradePrices)
            {
                optionalParams.Add(new KeyValuePair<string, string>("trade_prices", "1"));
            }

            return await DoPost<Trade>(RequestType.Trades, optionalParams);
        }

        //public async Task<Dictionary<string, Balance>> GetBalance()
        //{
        //    return await doPost<Balance>(RequestType.Balance);
        //}

        //public async Task<string> getHistoricalSummary(bool btc = false, int start = 0, int end = 0)
        //{
        //    List<KeyValuePair<string, string>> optionalParams = new List<KeyValuePair<string, string>>();

        //    if (btc)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("btc", "1"));
        //    }

        //    if (start > 0)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("start", start.ToString()));
        //    }

        //    if (end > 0)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("end", end.ToString()));
        //    }

        //    return await doPost<HistoricalSummary>(RequestType.HistoricalSummary);
        //}

        //public async Task<string> getHistoricalCurrency(string currency = null, int start = 0, int end = 0)
        //{
        //    List<KeyValuePair<string, string>> optionalParams = new List<KeyValuePair<string, string>>();

        //    if (!string.IsNullOrEmpty(currency))
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("currency", currency));
        //    }

        //    if (start > 0)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("start", start.ToString()));
        //    }

        //    if (end > 0)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("end", end.ToString()));
        //    }

        //    return await doPost(RequestType.HistoricalCurrency, optionalParams);
        //}

        //public async Task<string> getGroupedBalance(string group = "exchange", bool excludeDepWith = false, string type = null)
        //{
        //    List<KeyValuePair<string, string>> optionalParams = new List<KeyValuePair<string, string>>();

        //    if (!string.IsNullOrEmpty(group))
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("group", group));
        //    }

        //    if (!string.IsNullOrEmpty(type))
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("type", type));
        //    }

        //    if (excludeDepWith)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("exclude_dep_with", "1"));
        //    }

        //    return await doPost(RequestType.GroupedBalance, optionalParams);
        //}

        //public async Task<string> getGains(string price = null, bool btc = false)
        //{
        //    List<KeyValuePair<string, string>> optionalParams = new List<KeyValuePair<string, string>>();

        //    if (!string.IsNullOrEmpty(price))
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("price", price));
        //    }

        //    if (btc)
        //    {
        //        optionalParams.Add(new KeyValuePair<string, string>("btc", "1"));
        //    }

        //    return await doPost(RequestType.Gains, optionalParams);
        //}
    }
}