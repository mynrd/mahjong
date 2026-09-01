# Web

The Angular front end. It is built into the API's `wwwroot` and served from there - see the
[root README](../README.md) for how to run everything.

For front-end work only:

```powershell
npm install       # once
npx ng build      # writes into server/src/Mahjong.Api/wwwroot
```

`..\run.ps1 -Watch` rebuilds on every save while the server runs.
