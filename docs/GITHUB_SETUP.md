# GitHub connection checklist

The working directory is prepared for GitHub but should begin as a private repository.

## Before the first push

1. Choose the GitHub account or organization that will own the product.
2. Create an empty private repository without generating a README, license, or `.gitignore`.
3. Decide whether the product will remain proprietary or receive an open-source license. Do not add a license by assumption.
4. Confirm the default branch name is `main`.
5. Review `git status` and verify that `artifacts`, `logs`, `.codex-build`, `bin`, and `obj` are ignored.
6. Review the initial diff for application IDs or other data that should not be public.

## Recommended repository settings

- Enable secret scanning and push protection.
- Enable Dependabot alerts.
- Enable private vulnerability reporting.
- Require the Windows Build workflow before merging to `main`.
- Protect `main` from force pushes and deletion.
- Require pull requests after additional contributors are invited.
- Keep Actions permissions read-only by default unless a reviewed release workflow needs more.

## First connection commands

Replace the placeholder with the exact repository URL created in GitHub:

```powershell
git add .
git commit -m "Initial John’s Twitch Handler source"
git branch -M main
git remote add origin https://github.com/OWNER/REPOSITORY.git
git push -u origin main
```

Do not run the remote commands until the owner has confirmed the GitHub account, repository name, visibility, and license decision.
