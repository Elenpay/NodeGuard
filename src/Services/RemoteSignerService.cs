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

/*
 * NodeGuard
 * Copyright (C) 2023  ClovrLabs
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
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 */
 
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using FundsManager.Helpers;
using NBitcoin;

namespace FundsManager.Services;

public interface IRemoteSignerService
{
    Task<PSBT?> Sign(PSBT psbt);
}

/// <summary>
/// Service in charge with invoking the remote signer lambda function
/// </summary>
public class RemoteSignerServiceService : IRemoteSignerService
{
    private readonly ILogger<RemoteSignerServiceService> _logger;

    public RemoteSignerServiceService(ILogger<RemoteSignerServiceService> logger)
    {
        _logger = logger;
    }

    public async Task<PSBT?> Sign(PSBT psbt)
    {
        PSBT? result;
        try
        {
            //Check if ENABLE_REMOTE_SIGNER is set 
            if (!Constants.ENABLE_REMOTE_SIGNER)
            {
                _logger.LogWarning("Remote signer is disabled but was called");
                return null;
            }

            if (psbt == null) throw new ArgumentNullException(nameof(psbt));

            var region = Constants.AWS_REGION!; 
            //AWS Call to lambda function
            var awsAccessKeyId = Constants.AWS_ACCESS_KEY_ID; 
            var awsSecretAccessKey = Constants.AWS_SECRET_ACCESS_KEY;

            var credentials = new ImmutableCredentials(
                awsAccessKeyId,
                awsSecretAccessKey,
                null);

            var awsKmsKeyId = Constants.AWS_KMS_KEY_ID; 

            var requestPayload = new LightningService.Input(psbt.ToBase64(), SigHash.All,
                CurrentNetworkHelper.GetCurrentNetwork().ToString(),
                awsKmsKeyId ??
                throw new InvalidOperationException());

            var serializedPayload = JsonSerializer.Serialize(requestPayload);

            using var httpClient = new HttpClient();

            //We use a special lib for IAM Auth to AWS
            var signLambdaResponse = await httpClient.PostAsync(
                Constants.REMOTE_SIGNER_ENDPOINT!,
                new StringContent(serializedPayload,
                    Encoding.UTF8,
                    "application/json"),
                regionName: region,
                serviceName: "lambda",
                credentials: credentials);

            if (signLambdaResponse.StatusCode != HttpStatusCode.OK)
            {
                var errorWhileSignignPsbtWithAwsLambdaFunctionStatus =
                    $"Error while signing PSBT with AWS Lambda function,status code:{signLambdaResponse.StatusCode} error:{signLambdaResponse.ReasonPhrase}";
                _logger.LogError(errorWhileSignignPsbtWithAwsLambdaFunctionStatus);
                throw new Exception(errorWhileSignignPsbtWithAwsLambdaFunctionStatus);
            }

            var output =
                JsonSerializer.Deserialize<LightningService.Output>(
                    await signLambdaResponse.Content.ReadAsStreamAsync());

            if (!PSBT.TryParse(output.Psbt, CurrentNetworkHelper.GetCurrentNetwork(), out var finalSignedPsbt))
            {
                var errorWhileParsingPsbt = "Error while parsing PSBT signed from AWS Remote FundsManagerSigner";
                _logger.LogError(errorWhileParsingPsbt);
                throw new Exception(errorWhileParsingPsbt);
            }

            result = finalSignedPsbt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while signing PSBT");
            throw;
        }

        return result;
    }
}