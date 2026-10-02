#!/usr/bin/env bash
# 通过 GitHub git-data API 推送分支。
# 动机：本机 git 传输层推送会「Connection was reset」，且带 token 的 URL 同样失败。
# git-data API 走 HTTPS API 通道，绕过 git 传输层问题。
# 注意：所有大 JSON 必须走 --input 文件，不能用 -f（命令行长度限制）。
set -euo pipefail

REPO="YYiChen/XAssistant"
LOCAL="C:/Users/32126/WorkBuddy/2026-10-02-16-09-38/XAssistant"
BASE="39268e6073b8c2aca79732f54f75656267935762"
BRANCH="feat/personal-fork"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

cd "$LOCAL"

PY="C:/Users/32126/.workbuddy/binaries/python/versions/3.13.12/python.exe"

echo "[1/6] 取 base tree"
BASE_TREE=$(gh api "repos/$REPO/git/commits/$BASE" --jq '.tree.sha')
echo "      $BASE_TREE"

echo "[2/6] 收集改动文件并创建 blob"
FILES=$(git diff --name-only "$BASE" HEAD)
ENTRIES="[]"
while IFS= read -r f; do
  [ -z "$f" ] && continue
  "$PY" -c "
import base64,json,sys
data=open(sys.argv[1],'rb').read()
json.dump({'content':base64.b64encode(data).decode(),'encoding':'base64'},open(sys.argv[2],'w'))
" "$f" "$TMP/blob.json"
  SHA=$(gh api "repos/$REPO/git/blobs" --method POST --input "$TMP/blob.json" --jq '.sha')
  MODE=$(git ls-tree HEAD "$f" | awk '{print $1}')
  ENTRIES=$("$PY" -c "
import json,sys
e=json.loads(sys.argv[1])
e.append({'path':sys.argv[2],'mode':sys.argv[3],'type':'blob','sha':sys.argv[4]})
print(json.dumps(e))
" "$ENTRIES" "$f" "$MODE" "$SHA")
  echo "      $f -> ${SHA:0:7}"
done <<< "$FILES"

echo "[3/6] 创建 tree"
"$PY" -c "
import json,sys
json.dump({'base_tree':sys.argv[1],'tree':json.loads(sys.argv[2])},open(sys.argv[3],'w'))
" "$BASE_TREE" "$ENTRIES" "$TMP/tree.json"
TREE=$(gh api "repos/$REPO/git/trees" --method POST --input "$TMP/tree.json" --jq '.sha')
echo "      $TREE"

echo "[4/6] 创建 commit"
"$PY" -c "
import json,subprocess,sys
msg=subprocess.run(['git','log','-1','--pretty=%B'],capture_output=True,text=True).stdout
json.dump({'message':msg,'tree':sys.argv[1],'parents':[sys.argv[2]]},open(sys.argv[3],'w'))
" "$TREE" "$BASE" "$TMP/commit.json"
COMMIT=$(gh api "repos/$REPO/git/commits" --method POST --input "$TMP/commit.json" --jq '.sha')
echo "      $COMMIT"

echo "[5/6] 创建分支 ref"
"$PY" -c "
import json,sys
json.dump({'ref':'refs/heads/'+sys.argv[1],'sha':sys.argv[2]},open(sys.argv[3],'w'))
" "$BRANCH" "$COMMIT" "$TMP/ref.json"
gh api "repos/$REPO/git/refs" --method POST --input "$TMP/ref.json" --jq '.ref + "  ->  " + .object.sha'

echo "[6/6] 验证"
gh api "repos/$REPO/branches/$BRANCH" --jq '"分支: " + .name + "  最新提交: " + .commit.sha'

echo "推送完成"
