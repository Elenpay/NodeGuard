/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NodeGuard.Helpers;

namespace NodeGuard.Tests.Helpers;

public class HttpContextExtensionsTests
{
    [Fact]
    public void GetClientIpAddress_NullHttpContext_ReturnsNull()
    {
        // Arrange
        HttpContext? httpContext = null;

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetClientIpAddress_XForwardedForHeader_ReturnsFirstIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "192.168.1.100";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("192.168.1.100");
    }

    [Fact]
    public void GetClientIpAddress_XForwardedForHeaderWithMultipleIps_ReturnsFirstIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "192.168.1.100, 10.0.0.1, 172.16.0.1";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("192.168.1.100");
    }

    [Fact]
    public void GetClientIpAddress_XForwardedForHeaderWithSpaces_ReturnsTrimmedIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "  192.168.1.100  , 10.0.0.1";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("192.168.1.100");
    }

    [Fact]
    public void GetClientIpAddress_XRealIpHeader_ReturnsIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Real-IP"] = "10.0.0.50";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("10.0.0.50");
    }

    [Fact]
    public void GetClientIpAddress_BothHeaders_PrefersXForwardedFor()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "192.168.1.100";
        httpContext.Request.Headers["X-Real-IP"] = "10.0.0.50";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("192.168.1.100");
    }

    [Fact]
    public void GetClientIpAddress_RemoteIpAddressFallback_ReturnsRemoteIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.50");

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("203.0.113.50");
    }

    [Fact]
    public void GetClientIpAddress_EmptyXForwardedFor_FallsBackToXRealIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "";
        httpContext.Request.Headers["X-Real-IP"] = "10.0.0.50";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("10.0.0.50");
    }

    [Fact]
    public void GetClientIpAddress_EmptyHeaders_FallsBackToRemoteIp()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "";
        httpContext.Request.Headers["X-Real-IP"] = "";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("172.16.0.100");

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("172.16.0.100");
    }

    [Fact]
    public void GetClientIpAddress_NoHeadersNoRemoteIp_ReturnsNull()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetClientIpAddress_IPv6Address_ReturnsIpv6()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-For"] = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";

        // Act
        var result = httpContext.GetClientIpAddress();

        // Assert
        result.Should().Be("2001:0db8:85a3:0000:0000:8a2e:0370:7334");
    }
}
