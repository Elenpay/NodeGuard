add-license-cs:
    go install github.com/fbiville/headache/cmd/headache@latest
    headache --configuration ./configuration-cs.json
add-migration name:
    cd src && dotnet ef migrations add {{name}}
    