set fallback := true

drop-db:
    cd src && dotnet ef database drop -f --context ApplicationDbContext 
add-license-cs:
    go install github.com/fbiville/headache/cmd/headache@latest
    headache --configuration ./configuration-cs.json
add-migration name:
    cd src && dotnet ef migrations add --context ApplicationDbContext {{name}}
remove-migration:
    cd src && dotnet ef migrations remove --context ApplicationDbContext
mine:
    while true; do docker exec polar-n1-backend1 bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass -generate 1; sleep 60; done

dapr-run:
    dapr run --app-id nodeguard --app-port 50051 --dapr-grpc-port 33601 -- dotnet run --project src/NodeGuard.csproj --launch-profile "NodeGuard local debug"
    
dapr-stop:
    ps -ef | grep '[d]apr.*--app-id nodeguard' | awk '{print $2}' | xargs -r kill -9 && killall -9 NodeGuard