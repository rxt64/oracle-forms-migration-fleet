## What failed, or what is missing

<!-- The observable problem. If a real run exposed it, say what the run did and what it produced. -->

## What this changes

<!-- The effect on behaviour, not a file-by-file narration of the diff. -->

## Test that proves it

<!-- Name the test that fails without this change. If the change narrows a rule, also name the test
     that proves the rule still fires when it should. -->

```
dotnet test  →
```

## What this does not cover

<!-- Known gaps left open on purpose. "None" is a valid answer; silence is not. -->

## Checklist

- [ ] A test fails without this change
- [ ] `dotnet test` passes locally
- [ ] No secret, token, endpoint, or connection string is exposed to the browser
- [ ] Source classification still reads file **names** only, never contents
- [ ] `ExecutionAdapterConnected` is still `false`
- [ ] Deployed and smoke-tested, or explicitly not required
