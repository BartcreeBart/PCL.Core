using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Service;
using PCL.Core.Utils;

namespace PCL.Core.Helper;

public static class HttpRequest
{
    public static async Task<HttpResponseMessage> TryGetServerResponse(HttpRequestOptions options)
    {
        Exception? lastException = null;
        while (options.Retry >0)
        {
            try
            {
                using var request = options.GetRequestMessage();
                using var cts = new CancellationTokenSource(options.Timeout);
                return await HttpClientService.GetClient()
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                options.Retry--;
                LogWrapper.Error(ex, "Network", "发送可重试的网络请求失败");
            }
        }

        throw new HttpRequestException("发送网络请求失败", lastException);
    }

    public static async Task<HttpResponseMessage> GetServerResponse(HttpRequestOptions options)
    {
        HttpResponseMessage response = await TryGetServerResponse(options);
        response.EnsureSuccessStatusCode();
        return response;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        return await HttpClientService.GetClient().SendAsync(request);
    }
    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,HttpCompletionOption options)
    {
        return await HttpClientService.GetClient().SendAsync(request, options);
    }
    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,HttpCompletionOption options,CancellationToken token)
    {
        return await HttpClientService.GetClient().SendAsync(request, options,token);
    }
}
