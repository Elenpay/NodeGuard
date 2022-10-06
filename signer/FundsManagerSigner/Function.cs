using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using NBitcoin;

[assembly:
    LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<FundsManagerSigner.HttpApiJsonSerializerContext>))]

namespace FundsManagerSigner;

[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
public partial class HttpApiJsonSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// DTO used to deserialize the env var value which key is a master fingerprint of the PSBT
/// </summary>
public class SignPSBTConfig
{
    /// <summary>
    /// Encrypted seed phrase
    /// </summary>
    public string EncryptedSeedphrase { get; set; }

    /// <summary>
    /// AWS KMS Key Id used to decrypt the encrypted seedphrase
    /// </summary>
    public string AwsKmsKeyId { get; set; }
}

public class Function
{
    /// <summary>
    /// A lambda function that takes a psbt and signs it
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var response = new APIGatewayHttpApiV2ProxyResponse();

        try
        {
            var requestBody = JsonSerializer.Deserialize<SignPSBTRequest>(request.Body);
            if (requestBody == null) throw new ArgumentNullException(nameof(requestBody), "Request body not found");

            var kmsClient = new AmazonKeyManagementServiceClient();

            var network = requestBody.Network.ToUpper() switch
            {
                "REGTEST" => Network.RegTest,
                "MAINNET" => Network.Main,
                "TESTNET" => Network.TestNet,
                _ => throw new ArgumentException("Network not recognized")
            };

            var result = new SignPSBTResponse(null);

            if (PSBT.TryParse(requestBody.Psbt, network, out var parsedPSBT))
            {
                foreach (var psbtInput in parsedPSBT.Inputs)
                {
                    //We search for a fingerprint that can be used as a key for getting the config (env-var)
                    //Ideally, only fingerprints of the FundsManager signer wallet are set as env vars
                    var derivationPath = psbtInput.HDKeyPaths.Values.SingleOrDefault(x =>
                        Environment.GetEnvironmentVariable(x.MasterFingerprint.ToString()) != null);

                    if (derivationPath == null)
                    {
                        throw new ArgumentException(
                            "Invalid PSBT, the derivation path and the signing configuration cannot be found for none of the master fingerprints of all the pub keys",
                            nameof(derivationPath));
                    }

                    var inputPSBTMasterFingerPrint = derivationPath.MasterFingerprint;

                    var configJson = Environment.GetEnvironmentVariable(inputPSBTMasterFingerPrint.ToString());

                    var config = JsonSerializer.Deserialize<SignPSBTConfig>(configJson);

                    var decryptedSeed = await kmsClient.DecryptAsync(new DecryptRequest
                    {
                        CiphertextBlob = new MemoryStream(Convert.FromBase64String(config.EncryptedSeedphrase)),
                        EncryptionAlgorithm = EncryptionAlgorithmSpec.SYMMETRIC_DEFAULT,
                        KeyId = config.AwsKmsKeyId
                    });

                    if (decryptedSeed == null)
                    {
                        throw new ArgumentException("The seedphrase could not be decrypted / found", nameof(decryptedSeed));
                    }

                    var array = decryptedSeed.Plaintext.ToArray();

                    //The seedphrase words were originally splitted with @ instead of whitespaces due to AWS removing them on encryption
                    var seed = Encoding.UTF8.GetString(array).Replace("@", " ");

                    var extKey = new Mnemonic(seed).DeriveExtKey();
                    var bitcoinExtKey = extKey.GetWif(network);

                    var fingerPrint = bitcoinExtKey.GetPublicKey().GetHDFingerPrint();

                    if (fingerPrint != inputPSBTMasterFingerPrint)
                    {
                        var mismatchingFingerprint =
                            $"The master fingerprint from the input does not match the master fingerprint from the encrypted seedphrase master fingerprint";

                        throw new ArgumentException(mismatchingFingerprint, nameof(fingerPrint));
                    }

                    var partialSigsCount = parsedPSBT.Inputs.Sum(x => x.PartialSigs.Count);
                    //We can enforce the sighash for all the inputs in the request in case the PSBT was not modified or serialized correctly.
                    if (requestBody.EnforcedSighash != null)
                    {
                        psbtInput.SighashType = requestBody.EnforcedSighash;

                        Console.WriteLine($"Enforced sighash: {psbtInput.SighashType:G}");
                    }

                    var key = bitcoinExtKey
                        .Derive(derivationPath.KeyPath)
                        .PrivateKey;
                    psbtInput.Sign(key);

                    //We check that the partial signatures number has changed, otherwise finalize inmediately
                    var partialSigsCountAfterSignature =
                        parsedPSBT.Inputs.Sum(x => x.PartialSigs.Count);

                    if (partialSigsCountAfterSignature == 0 ||
                        partialSigsCountAfterSignature <= partialSigsCount)
                    {
                        var invalidNoOfPartialSignatures =
                            $"Invalid expected number of partial signatures after signing the PSBT";

                        throw new ArgumentException(
                            invalidNoOfPartialSignatures);
                    }

                    result = new SignPSBTResponse(parsedPSBT.ToBase64());

                    response = new APIGatewayHttpApiV2ProxyResponse()
                    {
                        Body = JsonSerializer.Serialize(result),
                        IsBase64Encoded = false,
                        StatusCode = 200
                    };
                }
            }
            Console.WriteLine($"Signing request finished");
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            response = new APIGatewayHttpApiV2ProxyResponse
            {
                Body = e.Message,
                IsBase64Encoded = false,
                StatusCode = 500
            };
        }

        return response;
    }

    /// <summary>
    /// Aux method used to generate an encrypted seed, it is added for generating new ones with a unit test
    /// </summary>
    /// <param name="mnemonicString"></param>
    /// <param name="keyId"></param>
    /// <returns>Base64 encrypted seedphrase</returns>
    public async Task<string> EncryptSeedphrase(string mnemonicString, string keyId)
    {
        if (string.IsNullOrWhiteSpace(mnemonicString))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(mnemonicString));
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(keyId));

        try
        {
            var mnemonic = new Mnemonic(mnemonicString);
        }
        catch (Exception e)
        {
            const string invalidMnemonicItContainsWhitespaces = "Invalid mnemonic";

            await Console.Error.WriteLineAsync(invalidMnemonicItContainsWhitespaces);

            await Console.Error.WriteLineAsync(e.Message);

            throw;
        }

        var kmsClient = new AmazonKeyManagementServiceClient();

        //To avoid KMS removing whitespaces and dismantling the seedphrase
        mnemonicString = mnemonicString.Replace(" ", "@");

        var encryptedSeed = await kmsClient.EncryptAsync(new EncryptRequest
        {
            EncryptionAlgorithm = EncryptionAlgorithmSpec.SYMMETRIC_DEFAULT,
            Plaintext = new MemoryStream(Encoding.UTF8.GetBytes(mnemonicString)), //UTF8 Encoding
            KeyId = keyId
        });

        var encryptedSeedBase64 = Convert.ToBase64String(encryptedSeed.CiphertextBlob.ToArray());

        return encryptedSeedBase64;
    }
}

public record SignPSBTRequest(string Psbt, SigHash? EnforcedSighash, string Network);
public record SignPSBTResponse(string? Psbt);