using System.Text;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramChannelParserTests
{
    private const string ProxiedHost = "104.223.85.164";

    private static string EncodePot(string origin)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(origin));
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// 仿照实测代理频道页裁剪：含日期分隔条、纯文本、纯图片、图片/视频重复引用、无正文无媒体的服务消息和超长标题消息。
    /// </summary>
    private static string BuildChannelPage() => $$"""
        <!DOCTYPE html>
        <html>
        <head><title>小蛙文案馆</title></head>
        <body>
        <div class="tgme_channel_info">
          <div class="tgme_channel_info_header_title">小蛙文案馆</div>
        </div>
        <div class="tgme_channel_history">
          <div class="tgme_widget_message_service_date_wrap">
            <div class="tgme_widget_message_service_date">September 24</div>
          </div>
          <div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="TGWenAnGuan/718">
            <div class="tgme_widget_message_text js-message_text" dir="auto">第一行正文<br>第二行正文</div>
            <a class="tgme_widget_message_date" href="https://{{ProxiedHost}}/TGWenAnGuan/718?__pot={{EncodePot("https://t.me")}}">
              <time datetime="2026-09-24T07:32:42+00:00">07:32</time>
            </a>
          </div>
          <div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="TGWenAnGuan/719">
            <a class="tgme_widget_message_photo_wrap" style="background-image:url('https://{{ProxiedHost}}/img/a.jpg?token=x&amp;__pot={{EncodePot("https://cdn5.telesco.pe")}}')"></a>
            <a class="tgme_widget_message_date" href="https://{{ProxiedHost}}/TGWenAnGuan/719?__pot={{EncodePot("https://t.me")}}">
              <time datetime="2026-09-25T08:00:00+00:00">08:00</time>
            </a>
          </div>
          <div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="TGWenAnGuan/720">
            <div class="tgme_widget_message_text js-message_text" dir="auto">带视频的消息</div>
            <video src="https://{{ProxiedHost}}/file/b.mp4?token=y&amp;__pot={{EncodePot("https://cdn5.telesco.pe")}}" class="tgme_widget_message_video blured js-message_video_blured"></video>
            <video src="https://{{ProxiedHost}}/file/b.mp4?token=y&amp;__pot={{EncodePot("https://cdn5.telesco.pe")}}" class="tgme_widget_message_video blured js-message_video_blured"></video>
            <a class="tgme_widget_message_date" href="https://{{ProxiedHost}}/TGWenAnGuan/720?__pot={{EncodePot("https://t.me")}}">
              <time datetime="2026-09-26T09:00:00+00:00">09:00</time>
            </a>
          </div>
          <div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="TGWenAnGuan/721">
            <div class="tgme_widget_message_video_wrap"></div>
          </div>
          <div class="tgme_widget_message_service" data-post="TGWenAnGuan/700">
            <div class="tgme_widget_message_service_text">channel created</div>
          </div>
          <div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="TGWenAnGuan/722">
            <div class="tgme_widget_message_text js-message_text" dir="auto">{{new string('长', 100)}}</div>
            <a class="tgme_widget_message_date" href="https://{{ProxiedHost}}/TGWenAnGuan/722?__pot={{EncodePot("https://t.me")}}">
              <time datetime="2026-09-27T10:00:00+00:00">10:00</time>
            </a>
          </div>
        </div>
        </body>
        </html>
        """;

    [Fact]
    public void ParseChannelPage_ReadsMessagesAndSkipsServiceAndEmptyOnes()
    {
        var messages = TelegramChannelParser.ParseChannelPage(BuildChannelPage(), "TGWenAnGuan");

        Assert.Equal(4, messages.Count);
        Assert.All(messages, message => Assert.StartsWith("https://t.me/", message.Url, StringComparison.Ordinal));
        Assert.Equal("来自 tg 频道 小蛙文案馆", messages[0].Notes);
        Assert.Equal("t.me", messages[0].SourceHost);
    }

    [Fact]
    public void ParseChannelPage_ReadsTextKeepingLineBreaksAndLeavesTitleAvailableForFutureUse()
    {
        var messages = TelegramChannelParser.ParseChannelPage(BuildChannelPage(), "TGWenAnGuan");

        var text = messages[0];
        Assert.Equal("https://t.me/TGWenAnGuan/718", text.Url);
        Assert.Empty(text.Title);
        Assert.Equal("第一行正文\n第二行正文", text.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T07:32:42+00:00"), text.PublishedAt);
        Assert.Empty(text.Media);

        var longMessage = messages[3];
        Assert.Equal(100, longMessage.Text.Length);
        Assert.Empty(longMessage.Title);
    }

    [Fact]
    public void ParseChannelPage_ReadsPhotoAndVideoWithOriginalUrls()
    {
        var messages = TelegramChannelParser.ParseChannelPage(BuildChannelPage(), "TGWenAnGuan");

        var photo = messages[1];
        Assert.Equal("https://t.me/TGWenAnGuan/719", photo.Url);
        Assert.Empty(photo.Title);
        var image = Assert.Single(photo.Media);
        Assert.Equal("image", image.Kind);
        Assert.Equal("https://cdn5.telesco.pe/img/a.jpg", image.OriginalUrl);
        Assert.Contains($"https://{ProxiedHost}/img/a.jpg?token=x&__pot=", image.ProxiedUrl, StringComparison.Ordinal);

        var video = messages[2];
        Assert.Equal("带视频的消息", video.Text);
        var clip = Assert.Single(video.Media);
        Assert.Equal("video", clip.Kind);
        Assert.Equal("https://cdn5.telesco.pe/file/b.mp4", clip.OriginalUrl);
        Assert.Contains($"https://{ProxiedHost}/file/b.mp4?token=y&__pot=", clip.ProxiedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseChannelPage_ReturnsEmptyForBlankHtml()
    {
        Assert.Empty(TelegramChannelParser.ParseChannelPage("<html><body></body></html>", "TGWenAnGuan"));
    }

    [Fact]
    public void TryParseSingleMessage_PrefersRestoredDateLinkThenFallsBackToGivenUrl()
    {
        var channelMessages = TelegramChannelParser.ParseChannelPage(BuildChannelPage(), "TGWenAnGuan");
        var first = TelegramChannelParser.TryParseSingleMessage(BuildChannelPage(), new Uri("https://t.me/s/TGWenAnGuan"));
        Assert.NotNull(first);
        Assert.Equal(channelMessages[0].Url, first.Url);
        Assert.Equal(channelMessages[0].Text, first.Text);

        var linked = TelegramChannelParser.TryParseSingleMessage($$"""
            <div class="tgme_widget_message js-widget_message">
              <div class="tgme_widget_message_text js-message_text" dir="auto">单条消息正文</div>
              <a class="tgme_widget_message_date" href="https://{{ProxiedHost}}/TGWenAnGuan/900?__pot={{EncodePot("https://t.me")}}">
                <time datetime="2026-09-24T07:32:42+00:00">07:32</time>
              </a>
            </div>
            """, new Uri("https://t.me/s/TGWenAnGuan"));
        Assert.NotNull(linked);
        Assert.Equal("https://t.me/TGWenAnGuan/900", linked.Url);
        Assert.Equal("单条消息正文", linked.Text);

        var fallback = new Uri("https://t.me/TGWenAnGuan/901");
        var bare = TelegramChannelParser.TryParseSingleMessage("""
            <div class="tgme_widget_message js-widget_message">
              <div class="tgme_widget_message_text js-message_text" dir="auto">没有链接的消息</div>
            </div>
            """, fallback);
        Assert.NotNull(bare);
        Assert.Equal(fallback.AbsoluteUri, bare.Url);
        Assert.Equal("没有链接的消息", bare.Text);

        Assert.Null(TelegramChannelParser.TryParseSingleMessage("<html><body></body></html>", fallback));
    }

    [Fact]
    public void TryRestoreOriginalUrl_DecodesPotParameterAndDropsOtherQueries()
    {
        var restored = TelegramChannelParser.TryRestoreOriginalUrl(
            $"https://{ProxiedHost}/file/xxx.mp4?token=abc&__pot={EncodePot("https://cdn5.telesco.pe")}");

        Assert.Equal("https://cdn5.telesco.pe/file/xxx.mp4", restored);
    }

    [Fact]
    public void TryRestoreOriginalUrl_ToleratesEscapedAmpersand()
    {
        var restored = TelegramChannelParser.TryRestoreOriginalUrl(
            $"https://{ProxiedHost}/file/xxx.mp4?token=abc&amp;__pot={EncodePot("https://cdn5.telesco.pe")}");

        Assert.Equal("https://cdn5.telesco.pe/file/xxx.mp4", restored);
    }

    [Theory]
    [InlineData("https://t.me/s/TGWenAnGuan")]
    [InlineData("https://cdn5.telesco.pe/file/xxx.mp4")]
    public void TryRestoreOriginalUrl_KeepsNonProxiedUrls(string url)
    {
        Assert.Equal(url, TelegramChannelParser.TryRestoreOriginalUrl(url));
    }

    [Fact]
    public void TryRestoreOriginalUrl_FallsBackWhenPotIsBroken()
    {
        var url = $"https://{ProxiedHost}/file/xxx.mp4?__pot=!!!not-base64!!!";

        Assert.Equal(url, TelegramChannelParser.TryRestoreOriginalUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void TryRestoreOriginalUrl_RejectsEmptyOrInvalid(string? url)
    {
        Assert.Null(TelegramChannelParser.TryRestoreOriginalUrl(url));
    }

    [Theory]
    [InlineData("t.me")]
    [InlineData("T.ME")]
    [InlineData("telegram.me")]
    [InlineData(".t.me")]
    [InlineData("example.com", false)]
    [InlineData("t.me.evil.com", false)]
    [InlineData("", false)]
    public void IsTelegramHost_MatchesOnlyTelegramDomains(string host, bool expected = true)
    {
        Assert.Equal(expected, TelegramChannelParser.IsTelegramHost(host));
    }

    [Fact]
    public void IsTelegramHost_ReturnsFalseForNull()
    {
        Assert.False(TelegramChannelParser.IsTelegramHost(null));
    }

    [Theory]
    [InlineData("https://t.me/s/TGWenAnGuan", true)]
    [InlineData("https://t.me/s/TGWenAnGuan/", true)]
    [InlineData("https://telegram.me/s/TGWenAnGuan", true)]
    [InlineData("https://t.me/TGWenAnGuan", false)]
    [InlineData("https://t.me/s", false)]
    [InlineData("https://example.com/s/TGWenAnGuan", false)]
    public void IsChannelPreviewUrl_DetectsChannelPreviewPath(string url, bool expected)
    {
        Assert.Equal(expected, TelegramChannelParser.IsChannelPreviewUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://t.me/s/TGWenAnGuan", "TGWenAnGuan")]
    [InlineData("https://t.me/s/TGWenAnGuan/", "TGWenAnGuan")]
    [InlineData("https://t.me/TGWenAnGuan", "t.me")]
    [InlineData("not a url", "")]
    public void ReadChannelName_ExtractsChannelSegment(string url, string expected)
    {
        Assert.Equal(expected, TelegramCollectorService.ReadChannelName(url));
    }
}
