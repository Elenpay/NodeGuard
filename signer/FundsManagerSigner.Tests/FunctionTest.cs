using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Xunit;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NBitcoin;

namespace FundsManagerSigner.Tests;

public class FunctionTest : IDisposable
{
    /// <summary>
    /// Aux method that sets the env vars for tests
    /// </summary>
    /// <returns></returns>
    public FunctionTest()
    {
        var config = new SignPSBTConfig
        {
            AwsKmsKeyId = "mrk-cec3e3ef59bc4616a6f44da60bfea0ba",
            EncryptedSeedphrase =
                "AQICAHheBtxW+2iTBvvhvmRXaxaScHh6up1/VWCRSMlopexrdwE1C/ylXBL5pmjJ3P/UG7XnAAABBzCCAQMGCSqGSIb3DQEHBqCB9TCB8gIBADCB7AYJKoZIhvcNAQcBMB4GCWCGSAFlAwQBLjARBAxPlkxPX65p7aRcXykCARCAgb4En2Bb/nWQ6m4i3JDP+KGjaGDAVF4LR6+2Ljl7orp6pfZbCCxK6e89OBpJWi7elQM670vD/SWkYSZ9MUWUshU8n7NyBJZuZgBhtaH6j6yDhgHtBv7cwJngv0d72QEaTrH2YqLCVuoddEKEpB13ezfkf56230QD134kcJze4fITQGA6sXxQ0x+WjKOeYltpB+Shk4+kaNja42ZM0MMjyrMOmQtXCkgdoTUVi6twiqU+qr8mQEEq0aNdZzlLCI/v"
        };
        var configJSon = JsonSerializer.Serialize(config);
        Environment.SetEnvironmentVariable("ed0210c8", configJSon);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ed0210c8", null);
    }

    [Fact]
    public async Task SignTest()
    {
        //Arrange
        var function = new Function();
        var context = new TestLambdaContext();

        //Test PSBT with 1 partial sig on input 0
        //JSON
        //{ "Psbt":"cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15\u002B6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP\u002BhqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po\u002BBAlGvFeBbuLfqwYlbP19H/\u002B/s2DIaAu8iKY\u002BJ0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6\u002BzUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn\u002B/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS\u002Bn7AgEDBAIAAAABBWlSIQLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI\u002BWNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF\u002BN0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==","Fingerprint":"ed0210c8","AccountDerivationPath":"m/48\u0027/1\u0027/1\u0027","AddressDerivationPath":"0/0","EnforcedSighash":1,"Network":"Regtest","AwsKmsKeyId":"mrk-cec3e3ef59bc4616a6f44da60bfea0ba"}
        var input = new SignPSBTRequest(
            "cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS+n7AgEDBAIAAAABBWlSIQLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI+WNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF+N0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==",
            SigHash.All,
            "Regtest");

        var inputJson = JsonSerializer.Serialize(input);

        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Body = inputJson
        };
        //Act
        var result = await function.FunctionHandler(request, context);

        var responseBody = JsonSerializer.Deserialize<SignPSBTResponse>(result.Body);

        var parsedPSBT = PSBT.Parse(responseBody.Psbt ?? throw new InvalidOperationException(), Network.RegTest);

        //Assert
        responseBody.Should().NotBeNull();
        parsedPSBT.Inputs.All(x => x.PartialSigs.Count == 2).Should().BeTrue();
        parsedPSBT.Inputs.All(x => x.SighashType == input.EnforcedSighash).Should().BeTrue();
    }

    [Fact]
    public async Task FailedSignTest_InvalidDerivationPath()
    {
        //Arrange
        var function = new Function();
        var context = new TestLambdaContext();

        //Test PSBT with 1 partial sig on input 0
        //JSON
        //{ "Psbt":"cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15\u002B6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP\u002BhqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po\u002BBAlGvFeBbuLfqwYlbP19H/\u002B/s2DIaAu8iKY\u002BJ0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6\u002BzUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn\u002B/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS\u002Bn7AgEDBAIAAAABBWlSIQLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI\u002BWNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF\u002BN0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==","Fingerprint":"ed0210c8","AccountDerivationPath":"m/48\u0027/1\u0027/1\u0027","AddressDerivationPath":"0/0","EnforcedSighash":1,"Network":"Regtest","AwsKmsKeyId":"mrk-cec3e3ef59bc4616a6f44da60bfea0ba"}
        var psbtBase64 = "cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS+n7AgEDBAIAAAABBWlSIQLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI+WNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF+N0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==";

        var psbt = PSBT.Parse(psbtBase64, Network.RegTest);

        foreach (var psbtInput in psbt.Inputs)
        {
            var temp = psbtInput.HDKeyPaths;
            psbtInput.HDKeyPaths.Clear(); ;
            foreach (var rootedKeyPath in temp)
            {
                psbtInput.AddKeyPath(rootedKeyPath.Key, new RootedKeyPath(rootedKeyPath.Value.MasterFingerprint, new KeyPath("m/48'")));
            }
        }

        var input = new SignPSBTRequest(
            psbt.ToBase64(),
            SigHash.All,
            "Regtest");

        var inputJson = JsonSerializer.Serialize(input);

        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Body = inputJson
        };
        //Act
        var result = await function.FunctionHandler(request, context);

        //Assert
        result.StatusCode.Should().Be(500);
        result.Body.Should().Contain("derivation");
    }

    [Fact]
    public async Task FailedSignTest_NoAddedPartialSig()
    {
        //Arrange
        var function = new Function();
        var context = new TestLambdaContext();

        //Test PSBT with 1 partial sig on input 0
        //JSON
        //{ "Psbt":"cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15\u002B6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP\u002BhqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po\u002BBAlGvFeBbuLfqwYlbP19H/\u002B/s2DIaAu8iKY\u002BJ0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6\u002BzUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn\u002B/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS\u002Bn7AgEDBAIAAAABBWlSIQLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI\u002BWNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF\u002BN0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==","Fingerprint":"ed0210c8","AccountDerivationPath":"m/48\u0027/1\u0027/1\u0027","AddressDerivationPath":"0/0","EnforcedSighash":1,"Network":"Regtest","AwsKmsKeyId":"mrk-cec3e3ef59bc4616a6f44da60bfea0ba"}
        var psbtBase64 = "cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS+n7AgEDBAIAAAABBWlSIQLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI+WNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF+N0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==";

        var psbt = PSBT.Parse(psbtBase64, Network.RegTest);

        foreach (var psbtInput in psbt.Inputs)
        {
            var temp = psbtInput.HDKeyPaths.ToList();
            psbtInput.HDKeyPaths.Clear(); ;
            foreach (var rootedKeyPath in temp)
            {
                psbtInput.AddKeyPath(rootedKeyPath.Key, new RootedKeyPath(rootedKeyPath.Value.MasterFingerprint, new KeyPath("m/48'")));
            }
        }

        var input = new SignPSBTRequest(
            psbt.ToBase64(),
            SigHash.All,
            "Regtest");

        var inputJson = JsonSerializer.Serialize(input);

        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Body = inputJson
        };
        //Act
        var result = await function.FunctionHandler(request, context);

        //Assert
        result.StatusCode.Should().Be(500);
        result.Body.Should().Contain("partial");
    }

    [Fact]
    public async Task GenerateEncryptedSeedTest()
    {
        //Arrange
        var function = new Function();
        var context = new TestLambdaContext();

        var mnemonicString =
            "middle teach digital prefer fiscal theory syrup enter crash muffin easily anxiety ill barely eagle swim volume consider dynamic unaware deputy middle into physical";

        var keyId = "mrk-cec3e3ef59bc4616a6f44da60bfea0ba";
        //Act
        var result = await function.EncryptSeedphrase(mnemonicString, keyId);
        var base64Decoding = Convert.FromBase64String(result);
        //Assert
        result.Should().NotBeEmpty();

        base64Decoding.Should().NotBeEmpty();
    }
}