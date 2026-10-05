static class TestEnvironment
{
    public static void RequireLive()
    {
        if(Environment.GetEnvironmentVariable("QUOTAWIDGET_LIVE_TESTS")!="1")
            throw new SkipTestException("optional installed-CLI check; set QUOTAWIDGET_LIVE_TESTS=1 to enable");
    }
}
sealed class SkipTestException(string message):Exception(message);
