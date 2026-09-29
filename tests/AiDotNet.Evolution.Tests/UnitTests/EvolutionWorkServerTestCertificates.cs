namespace AiDotNet.Evolution.Tests;

/// <summary>
/// TEST-ONLY self-signed certificates for the .NET Framework run of <see cref="EvolutionWorkServerTests"/>.
/// </summary>
/// <remarks>
/// .NET Framework 4.7.1 has no <c>CertificateRequest</c>, so its TLS path (the <c>#else</c> branches of the work server
/// and client) could not be exercised without certificates made elsewhere. These were generated for these tests only
/// (CN=evolution-work-test and CN=evolution-work-test-other, valid ten years, PFX without a password); they protect
/// nothing and are trusted by nothing. Newer targets keep creating a fresh certificate per run.
/// </remarks>
internal static class EvolutionWorkServerTestCertificates
{
    internal const string Server =
        "MIIJSgIBAzCCCQYGCSqGSIb3DQEHAaCCCPcEggjzMIII7zCCBZAGCSqGSIb3DQEHAaCCBYEEggV9MIIFeTCCBXUGCyqGSIb3DQEM" +
        "CgECoIIE7jCCBOowHAYKKoZIhvcNAQwBAzAOBAj1Oii9Iq1LAwICB9AEggTI7KFWO+ca8na5MHP0i77nNhAvakCNRZOi+qEdrMzS" +
        "ougwoVufyZog0xHYX4pGUC5J3s5qkwW9ON1+w6M4w2sb3gBt84+GoLN+NY182fOIWSYCcwj5ws/lCFg/PxnVsVyFflmSMBK3uWlr" +
        "zAl+5Ws1Sbm3OrY+LlbVxG2BC6gLLFB6pWsu1thw87I2KKARMiGyAATmfOlHzesE7+wF96qbRzMBRIFnc03sL6DbA63RwOWBeC3A" +
        "b5j4714DFlyjEpDV7ux/yC7F+ZQ/IO7zfSlKnylSq5mzfWdC8cnbFBSY6nePPBBACiS+m+avKQtviZxIj215jom4GJxNAPGkTcRN" +
        "r/tLtx61uWQBcf2f64oZpEW3ATvts8RlR5XoiKMq270zAIN5awf6tB/OzKt2Ngmwkk9CiaaCwPf1/LRJid3pMcy2V4rf5WB33+dd" +
        "feb4TtxsnzBpcV9yBOK2qX4J/ELVjdFaKWb0uiWNT4KDD/Jp6P74KG5ZEVi7RnZicn4Fgba7dcPuSAlLOcdSgLpNpLvWRWY//4Ys" +
        "Jpm1BpNA1c0sGb6SKcTSI5jW8FVRMjdM/hf0VmBa/ByuiqssAPQJv5PIR/oGPlzJyetUpU3ejUsoL4KiZZdHOiwUtcFhDRWypmvr" +
        "ofyLCIOuIobgZk9lJaZWt4183L4yXTLQeAFlrhHigvoxJbXsUXtNcGgdXBGdQ52Nwy/WKn/6oaf9ucyz3CdP3Jw956dFrCr2SrEA" +
        "PGDdA5b+nteFyhuwXgMj3By1sJ7dd4zRiGO6F3NSEBvYMZiyZkBeBRzMFO21r31PfWwOgU5HXIZ5n0vV2KEZcf3cFkQRt8nufefw" +
        "gCC0VNBguf/L5hD7fp9/D2pwJjdNBTaqVtlk/Ps1PY6HRZMCUQ35f3lpfbCLty1k97weT2AnS66oYjWK+4mSq82HCAwypeecL6dx" +
        "anx6zIaKiE/+R0pmOAB5+bL3TdFMENdQ1plT9M9yGsud3YRvGJtc++L498BesFMrdz2R38wCVgV54L89eRvL2esld8aYZevUAHdu" +
        "Z9y/cUR82TXTZCoEcq1uq9STbJgIIAgvHDoUwPPm+k1BC8uhI7dgiEL7SUZ0ozrpu/iT500K922Cwjt19NEvKfczS7G7wZRcAMzD" +
        "NPZsKILMC+Bf3xgMV/cTBljv3Gxs+YdqTUCpfvfj1xKGR9vtsxIj6czCo0tqdhraOUfLxn1rEMZFK2+n3uPtbOB7JzVpR+2ZdTC5" +
        "SOtJnd31nF9UY6CG5huw2WIU80Gw5pgq1HmbklzarDOA2Jr5bV447Ecsu5i5qHsZEPdK2X/oSi8QfwvlneiywDCNjZKnN9j2GhXO" +
        "oRE3KHVOonT5wPDtyoiGoeGuS3MseHH5DxQS083whia7VBdpt97tBaJUK29+kaTUJB+nAY1JCCqRHjzZvwcp9xgMz2z5dBbu6r46" +
        "o6Ws0oGuaQYyEOJsVnU+USmiCjqmnO8UDjHh5Z2Ej+eBDrUvsrdkwJcHTWbW2GEmvwTZ2ayrw/+4SkvmBt2EYVoTqOpYIvNgbjwj" +
        "UZdl+BL8A4p7wq91ScrpI96E9lb31xu859NtQ3cqqZ/im6aDAnmJxfdxsy7T9zS26DYqJaQElBuvm5zfBRJRBs5A8QklMXQwEwYJ" +
        "KoZIhvcNAQkVMQYEBAEAAAAwXQYJKwYBBAGCNxEBMVAeTgBNAGkAYwByAG8AcwBvAGYAdAAgAFMAbwBmAHQAdwBhAHIAZQAgAEsA" +
        "ZQB5ACAAUwB0AG8AcgBhAGcAZQAgAFAAcgBvAHYAaQBkAGUAcjCCA1cGCSqGSIb3DQEHBqCCA0gwggNEAgEAMIIDPQYJKoZIhvcN" +
        "AQcBMBwGCiqGSIb3DQEMAQMwDgQIxyCMG8mBevECAgfQgIIDEOhwvOAmW/t3CypvZTNhzV2cfG7AFp9/XSGysHXgb1UtghudsDR1" +
        "qi98K4FLk38708s8/4P5Fxn1Cix1Gs9xsSlLSda1uqEVU5A9kCZkPYZ32ZMAsbnoVEfi+y74et6hg5Unr9ktlg97Vpwqy1MWo3sG" +
        "pxAKqce3qbLY9A0Ws4DzucIOyJ5qxnUpK6tE4TwBK+LCBH8u9Yn0dfAKMgYTs66yuWHnQd3MQz7OL4BuCNq7qT7qrFk1rf+E2IA4" +
        "QoBjzE5v1zUmPWZQkdHKypfOfqcDTj53pGlO5EpYR4eybT0FbdUfdWv6mT8fqVL0/XtGcJpFOw6+KZgKJMx+Ok6rImM2nNdhFmEa" +
        "jpGLkSnEf6CcysTK0PfwuL2JoSSYckLBE3eLNwuyrf5N+fcK53TsKDUlMsQsmFP4dxdhtTns69sPtXAOHRjDU5ssmsZAaSSEFuQH" +
        "Gai3koBrRKc/Kc1KUllHQu/lYkLT+22HK7h0pemQAA05YX10eU2atZtkfn0RKfpwk2O4+sraDGnGIg0EYOhVfxhgPD9lsNXYCiqt" +
        "uUzBVPW+tEYG7BRq5horZ30TgnYQSO7Zg9nPC0b3Vpg7HPTwXJADNnoMi43yUwA7/wfSnav3HdlJ5JqW2IpFlz/mmFeVbpFWNj0L" +
        "AP9GV6yv5BIGeKPaP2Lzt7ujGeo3CgmaymBrqcpvehLTJ4MpmBVoAWH5BpZd6ajEfk6xtRcSR5FyP+/WZDz3vC/kACq4WaswLK/l" +
        "+BGSy5QaUTB2c7ahQRDA4Ydwpfel2ajpDxmEcoPWtssYAfo8HrEYSncW+aSdDGbF6Ohm5IS+DQTv71Y/+UrtKEhicB4w8KbZvF/q" +
        "1STaxnun+32tKc+dpC5nMBXtCvCEcIpg04Oc9zFpybTw+JipNC31E+BHazIgpUzBd/B9XN7O2PjIcYeR3tY4+GC6pEQvGijBYvHD" +
        "ZlU0ymsFSsw1/71EkS6MMCk6GLTQ+CI0SskvDOvOtb1DckNU3JUF8E4SRMhfLL9fy5A7iUkqwo+tLzImKswXhM6F3N09MGcwOzAf" +
        "MAcGBSsOAwIaBBTrhglwUFraSlPk8Sj2WBMah72YiAQUHcpi+jXlG6CFAC5AZcf2s6bqNkACAgfQ";

    internal const string Other =
        "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";
}