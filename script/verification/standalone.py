#!/usr/bin/env python3
"""Isolated standalone acceptance. Python stdlib + Docker; never builds the Host."""
import argparse
import concurrent.futures
import datetime
import hashlib
import hmac
import http.server
import json
import os
from pathlib import Path
import re
import secrets
import socket
import stat
import struct
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zlib

LABEL = "io.emenda.standalone-verification.owner"
ROOT = Path(__file__).resolve().parents[2] if len(Path(__file__).resolve().parents) > 2 else Path("/app")
IMAGES = {"pg": "postgres:15-alpine", "s3": "chrislusf/seaweedfs:3.85",
          "vl": "python:3.13-alpine", "host": "mcr.microsoft.com/dotnet/aspnet:10.0"}
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class Failure(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Failure(message)


def private_json(path, data):
    path = Path(path)
    require(not path.is_symlink(), "Refusing a symlink output file")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(data, stream, indent=2)
        stream.write("\n")


def check_location(path):
    path = Path(path).expanduser().resolve()
    require(not path.is_relative_to(ROOT), "Fixture and results must live outside the repository")
    require(path.parent.is_dir(), "Fixture parent directory must already exist")
    require(stat.S_IMODE(path.parent.stat().st_mode) & 0o077 == 0,
            "Fixture parent directory must be private (0700)")
    return path


def load_fixture(path):
    path = check_location(path)
    require(not path.is_symlink() and path.is_file(), "Fixture must be a regular file")
    require(stat.S_IMODE(path.stat().st_mode) & 0o077 == 0, "Fixture must be private (0600)")
    data = json.loads(path.read_text())
    require(re.fullmatch(r"emenda-verify-[0-9a-f]{16}", data.get("owner", "")), "Invalid fixture owner")
    require(data.get("version") == 1, "Unsupported fixture version")
    require(data["network"] == data["owner"], "Fixture network owner mismatch")
    for role, name in data["containers"].items():
        require(name == data["owner"] + "-" + role, "Fixture container owner mismatch")
    return path, data


def command(args, env=None, input_data=None, timeout=180):
    try:
        result = subprocess.run(args, env=env, input=input_data, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        raise Failure("Infrastructure command unavailable or timed out") from None
    require(result.returncode == 0, "Infrastructure command failed: " + Path(args[0]).name + " " + (args[1] if len(args) > 1 else "") + " (exit " + str(result.returncode) + "; sensitive output suppressed)")
    return result.stdout.decode(errors="replace").strip()


def docker(fixture, *args, input_data=None, timeout=180):
    env = {k: v for k, v in os.environ.items() if k not in ("DOCKER_CONTEXT", "DOCKER_HOST")}
    env["DOCKER_HOST"] = fixture["docker_host"]
    return command(["docker", *args], env=env, input_data=input_data, timeout=timeout)


def owned(fixture, name, kind="container"):
    result = docker(fixture, kind, "inspect", name, "--format", "{{json .Labels}}" if kind == "network"
                    else "{{json .Config.Labels}}")
    require((json.loads(result) or {}).get(LABEL) == fixture["owner"], "Resource owner label mismatch")


def request(url, method="GET", body=None, headers=None, timeout=30):
    try:
        with HTTP.open(urllib.request.Request(url, data=body, headers=headers or {}, method=method),
                       timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except (OSError, TimeoutError):
        return 0, b""


def wait_until(probe, description, seconds=90):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if probe():
            return
        time.sleep(0.5)
    raise Failure(description + " did not become ready")


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def bucket_request(fixture):
    """Create a private bucket using a real AWS SigV4 authenticated request."""
    endpoint = fixture["s3_endpoint"]
    path = "/" + fixture["bucket"]
    now = datetime.datetime.now(datetime.timezone.utc)
    stamp, date = now.strftime("%Y%m%dT%H%M%SZ"), now.strftime("%Y%m%d")
    digest = hashlib.sha256(b"").hexdigest()
    canonical_headers = "host:" + endpoint + "\nx-amz-content-sha256:" + digest + "\nx-amz-date:" + stamp + "\n"
    signed_headers = "host;x-amz-content-sha256;x-amz-date"
    canonical = "PUT\n" + path + "\n\n" + canonical_headers + "\n" + signed_headers + "\n" + digest
    scope = date + "/us-east-1/s3/aws4_request"
    to_sign = "AWS4-HMAC-SHA256\n" + stamp + "\n" + scope + "\n" + hashlib.sha256(canonical.encode()).hexdigest()
    key = ("AWS4" + fixture["s3_secret_key"]).encode()
    for value in (date, "us-east-1", "s3", "aws4_request"):
        key = hmac.new(key, value.encode(), hashlib.sha256).digest()
    signature = hmac.new(key, to_sign.encode(), hashlib.sha256).hexdigest()
    authorization = "AWS4-HMAC-SHA256 Credential=" + fixture["s3_access_key"] + "/" + scope + ", SignedHeaders=" + signed_headers + ", Signature=" + signature
    code, _ = request("http://" + endpoint + path, "PUT", b"", {"Authorization": authorization,
                      "x-amz-date": stamp, "x-amz-content-sha256": digest})
    require(code == 200, "Private S3 bucket creation failed")


def prepare(args):
    path = check_location(args.fixture)
    require(not path.exists(), "Refusing to overwrite an existing fixture")
    owner = "emenda-verify-" + secrets.token_hex(8)
    fixture = {"version": 1, "owner": owner, "network": owner,
               "containers": {role: owner + "-" + role for role in ("pg", "s3", "vl", "host")},
               "created": [], "status": "preparing", "images": IMAGES,
               "pg_password": secrets.token_urlsafe(32), "s3_access_key": secrets.token_hex(12),
               "s3_secret_key": secrets.token_urlsafe(32), "api_keys": [secrets.token_urlsafe(40), secrets.token_urlsafe(40)],
               "vl_key": secrets.token_urlsafe(32), "bucket": owner,
               "ports": {"pg": port(), "s3": port(), "host": port()},
               "owned_profile": False, "colima_profile": args.colima_profile}
    if args.docker_host:
        fixture["docker_host"] = args.docker_host
    else:
        profile = args.colima_profile or owner
        fixture["colima_profile"] = profile
        fixture["owned_profile"] = not bool(args.colima_profile)
        fixture["docker_host"] = "unix://" + str(Path.home() / ".colima" / profile / "docker.sock")
    fixture["s3_endpoint"] = "127.0.0.1:" + str(fixture["ports"]["s3"])
    fixture["db_connection"] = "Host=127.0.0.1;Port=" + str(fixture["ports"]["pg"]) + ";Database=emenda;Username=emenda;Password=" + fixture["pg_password"]
    private_json(path, fixture)
    if fixture["owned_profile"]:
        command(["colima", "start", fixture["colima_profile"], "--activate=false", "--ssh-config=false",
                 "--cpu", "2", "--memory", "3", "--disk", "12", "--root-disk", "8", "--mount", "none"], timeout=600)
    docker(fixture, "info", "--format", "{{.ServerVersion}}")
    for image in IMAGES.values():
        try:
            docker(fixture, "image", "inspect", image)
        except Failure:
            docker(fixture, "pull", image, timeout=600)
    create_dependencies(path, fixture)


def create_dependencies(path, fixture):
    owner = fixture["owner"]
    docker(fixture, "network", "create", "--label", LABEL + "=" + owner, owner)
    fixture["created"].append("network")
    private_json(path, fixture)
    for role in ("pg", "s3", "vl"):
        name = fixture["containers"][role]
        argv = ["create", "--name", name, "--label", LABEL + "=" + owner, "--network", owner]
        if role == "pg":
            argv += ["-p", "127.0.0.1:" + str(fixture["ports"]["pg"]) + ":5432", "--env", "POSTGRES_PASSWORD",
                     "--env", "POSTGRES_USER", "--env", "POSTGRES_DB", IMAGES[role]]
            container_env = {"POSTGRES_PASSWORD": fixture["pg_password"], "POSTGRES_USER": "emenda", "POSTGRES_DB": "emenda"}
        elif role == "s3":
            argv += ["-p", "127.0.0.1:" + str(fixture["ports"]["s3"]) + ":8333", IMAGES[role],
                     "server", "-dir=/data", "-ip=127.0.0.1", "-ip.bind=0.0.0.0", "-volume.max=8", "-master.volumeSizeLimitMB=64", "-s3", "-s3.port=8333", "-s3.config=/etc/verification-s3.json"]
            container_env = {}
        else:
            argv += ["--env", "VL_CONTROL_KEY", IMAGES[role], "python", "/verification.py", "serve-vl"]
            container_env = {"VL_CONTROL_KEY": fixture["vl_key"]}
        # docker --env NAME reads from this subprocess environment, never argv secrets.
        env = {**os.environ, **container_env, "DOCKER_HOST": fixture["docker_host"]}
        env.pop("DOCKER_CONTEXT", None)
        command(["docker", *argv], env=env)
        fixture["created"].append(role)
        private_json(path, fixture)
        if role == "s3":
            config_path = path.parent / (owner + "-s3.json")
            private_json(config_path, {"identities": [{"name": owner, "credentials": [{"accessKey": fixture["s3_access_key"], "secretKey": fixture["s3_secret_key"]}], "actions": ["Admin", "Read", "Write", "List", "Tagging"]}]})
            docker(fixture, "cp", str(config_path), name + ":/etc/verification-s3.json")
        if role == "vl":
            docker(fixture, "cp", str(Path(__file__).resolve()), name + ":/verification.py")
        docker(fixture, "start", name)
    def pg_ready():
        try:
            docker(fixture, "exec", fixture["containers"]["pg"], "pg_isready", "-U", "emenda", "-d", "emenda")
            with socket.create_connection(("127.0.0.1", fixture["ports"]["pg"]), timeout=1):
                return True
        except (Failure, OSError):
            return False
    wait_until(pg_ready, "PostgreSQL 15")
    wait_until(lambda: request("http://" + fixture["s3_endpoint"])[0] == 403, "Private SeaweedFS S3")
    bucket_request(fixture)
    require(sql(fixture, "SHOW server_version;").startswith("15."), "PostgreSQL major version must be 15")
    fixture["status"] = "ready"
    private_json(path, fixture)
    print("Dependencies ready; private fixture: " + str(path))


def sql(fixture, query):
    owned(fixture, fixture["containers"]["pg"])
    return docker(fixture, "exec", "-i", fixture["containers"]["pg"], "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1",
                  "-U", "emenda", "-d", "emenda", input_data=query.encode())


def png(color):
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xffffffff)
    rows = b"".join(b"\x00" + bytes(color) * 32 for _ in range(32))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 32, 32, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


def multipart(student, key, images):
    boundary = "emenda" + secrets.token_hex(16)
    parts = []
    for field, value in (("studentId", student), ("sourceKey", key)):
        parts.append(("--" + boundary + '\r\nContent-Disposition: form-data; name="' + field + '"\r\n\r\n' + value + "\r\n").encode())
    for index, image in enumerate(images):
        parts += [("--" + boundary + '\r\nContent-Disposition: form-data; name="images"; filename="page-' + str(index) + '.png"\r\nContent-Type: image/png\r\n\r\n').encode(), image, b"\r\n"]
    parts.append(("--" + boundary + "--\r\n").encode())
    return b"".join(parts), "multipart/form-data; boundary=" + boundary


def json_body(body):
    try:
        return json.loads(body)
    except (ValueError, UnicodeError):
        raise Failure("HTTP response was not the required JSON contract") from None


def api(fixture, route, method="GET", data=None, caller=0, raw=None, content_type=None):
    headers = {}
    if caller is not None:
        headers["X-Emenda-Api-Key"] = fixture["api_keys"][caller]
    body = raw
    if data is not None:
        body = json.dumps(data).encode()
        content_type = "application/json"
    if content_type:
        headers["Content-Type"] = content_type
    return request("http://127.0.0.1:" + str(fixture["ports"]["host"]) + route, method, body, headers, timeout=120)


def upload(fixture, student, key, images, caller=0):
    body, content_type = multipart(student, key, images)
    status, body = api(fixture, "/api/intake/uploads", "POST", caller=caller, raw=body, content_type=content_type)
    return status, json_body(body)


def upload_retry(fixture, student, key, images, caller=0):
    deadline = time.monotonic() + 120
    while True:
        result = upload(fixture, student, key, images, caller)
        if result[0] != 409 or result[1].get("errorCode") != "source_in_progress":
            return result
        require(time.monotonic() < deadline, "Concurrent upload remained source_in_progress")
        time.sleep(0.25)


def completed(result, expected_items, expected_images):
    status, body = result
    code = body.get("errorCode")
    safe_code = code if code in {"invalid_request", "source_conflict", "source_in_progress", "vl_failed", "processing_failed"} else "unclassified"
    require(status == 200 and body.get("success") is True,
            "Upload did not fully succeed (HTTP " + str(status) + ", errorCode=" + safe_code + ")")
    require(isinstance(body.get("sourceId"), str) and bool(body["sourceId"]), "Upload missing opaque sourceId")
    items = body.get("items", [])
    require(len(items) == expected_items, "Upload item count mismatch")
    require(len({item["id"] for item in items}) == expected_items, "Duplicate item IDs")
    require(sum(len(item.get("imagePaths", [])) for item in items) == expected_images, "Upload did not cover the entire image batch")
    return body


def identity(body):
    return body["sourceId"], sorted(item["id"] for item in body["items"])


def envelope(result):
    status, raw = result
    body = json_body(raw)
    require(status == 200 and body.get("success") is True and "data" in body, "Business envelope failed")
    return body["data"]


def host_env(fixture):
    return {"ASPNETCORE_ENVIRONMENT": "Standalone", "DOTNET_ENVIRONMENT": "Standalone",
            "Emenda__Runtime__Mode": "Standalone", "Emenda__Database__SchemaOwnership": "Own",
            "ConnectionStrings__Emenda": "Host=" + fixture["containers"]["pg"] + ";Port=5432;Database=emenda;Username=emenda;Password=" + fixture["pg_password"],
            "Emenda__Security__Enabled": "true", "Emenda__Security__AllowAnonymous": "false",
            "Emenda__Security__Callers__0__Name": "verification-a", "Emenda__Security__Callers__0__ApiKey": fixture["api_keys"][0],
            "Emenda__Security__Callers__1__Name": "verification-b", "Emenda__Security__Callers__1__ApiKey": fixture["api_keys"][1],
            "Emenda__Security__DynamicCallers__Path": "/tmp/emenda-verification-callers.json",
            "Mistake__Http__Port": "5007", "Mistake__Student__Enabled": "false",
            "Mistake__Workers__UploadPollingWorker__Enabled": "false", "Mistake__Workers__ReanalyzeJobHostedService__Enabled": "false",
            "Mistake__Workers__ReanalyzeJobCleanupService__Enabled": "false", "Mistake__Layout__Enabled": "false",
            "Mistake__SourceIntake__Enabled": "false", "Mistake__SourceTargets__Enabled": "false",
            "Mistake__SourceReturns__Enabled": "false", "Mistake__SourceHandoffs__Enabled": "false", "Mistake__LegacyReturns__Enabled": "false",
            "Oss__InternalEndpoint": fixture["containers"]["s3"] + ":8333", "Oss__InternalSecure": "false",
            "Oss__AccessKey": fixture["s3_access_key"], "Oss__SecretKey": fixture["s3_secret_key"], "Oss__BucketName": fixture["bucket"],
            "Oss__PublicBaseUrl": "http://" + fixture["s3_endpoint"],
            "VlAnalyzer__ApiKey": fixture["vl_key"], "VlAnalyzer__BaseUrl": "http://" + fixture["containers"]["vl"] + ":8080/v1",
            "VlAnalyzer__Model": "verification-deterministic", "VlAnalyzer__MaxRetries": "0", "VlAnalyzer__TimeoutSeconds": "10"}


def start_host(path, fixture, host_dir):
    name = fixture["containers"]["host"]
    config = host_env(fixture)
    env = {**os.environ, **config, "DOCKER_HOST": fixture["docker_host"]}
    env.pop("DOCKER_CONTEXT", None)
    argv = ["docker", "create", "--name", name, "--label", LABEL + "=" + fixture["owner"], "--network", fixture["network"],
            "-p", "127.0.0.1:" + str(fixture["ports"]["host"]) + ":5007", "--workdir", "/app"]
    for key in config:
        argv += ["--env", key]
    argv += [fixture["images"]["host"], "dotnet", "/app/Emenda.Host.dll"]
    command(argv, env=env)
    fixture["created"].append("host")
    private_json(path, fixture)
    docker(fixture, "cp", str(host_dir) + "/.", name + ":/app/")
    caller_path = path.parent / (fixture["owner"] + "-callers.json")
    private_json(caller_path, {"version": 1, "callers": []})
    docker(fixture, "cp", str(caller_path), name + ":/tmp/emenda-verification-callers.json")
    docker(fixture, "start", name)
    wait_until(lambda: api(fixture, "/health/live", caller=None)[0] == 200, "Actual dotnet Host liveness", seconds=120)
    wait_until(lambda: api(fixture, "/health/ready", caller=None)[0] == 200, "Actual dotnet Host readiness", seconds=120)


def remove_container(fixture, role):
    if role not in fixture["created"]:
        return
    name = fixture["containers"][role]
    owned(fixture, name)
    docker(fixture, "rm", "-fv", name)
    fixture["created"].remove(role)


def control_vl(fixture, mode):
    # Control endpoint is network-internal and requires a generated bearer secret.
    code = "import os,urllib.request; r=urllib.request.Request('http://127.0.0.1:8080/control', data=b'" + mode + "', headers={'Authorization':'Bearer '+os.environ['VL_CONTROL_KEY']}, method='POST'); urllib.request.urlopen(r).read()"
    owned(fixture, fixture["containers"]["vl"])
    docker(fixture, "exec", fixture["containers"]["vl"], "python", "-c", code)


def count_items(fixture):
    return int(sql(fixture, "SELECT count(*) FROM mistake_item;"))


def run(args):
    path, fixture = load_fixture(args.fixture)
    require(fixture["status"] == "ready", "Fixture is not prepared or was already used; prepare a fresh fixture")
    host_dir = Path(args.host_dir).expanduser().resolve()
    require(all((host_dir / name).is_file() for name in ("Emenda.Host.dll", "Emenda.Host.runtimeconfig.json", "Emenda.Host.deps.json")),
            "--host-dir must contain a prebuilt runnable Host (publish output recommended)")
    results_path = check_location(args.results) if args.results else path.parent / "results.json"
    require(results_path != path, "Results must not overwrite the private fixture")
    results = {"owner": fixture["owner"], "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
               "mode": "Standalone/Own", "database": "PostgreSQL 15", "storage": "real SeaweedFS/private S3",
               "vision": "deterministic local protocol fixture, not a real model", "checks": [], "success": False}
    def passed(name, **evidence):
        results["checks"].append({"name": name, "passed": True, **evidence})
        private_json(results_path, results)
        print("PASS " + name)
    fixture["status"] = "running"
    private_json(path, fixture)
    try:
        require(sql(fixture, "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';") == "0", "Own startup requires a genuinely empty database")
        start_host(path, fixture, host_dir)
        require(sql(fixture, "SELECT to_regclass('public.product_intake_request') IS NOT NULL;") == "t", "Own startup did not apply the product intake migration")
        passed("empty_database_own_startup_liveness_readiness")
        require(api(fixture, "/api/mistakes/pending-reviews?page=1&size=10", caller=None)[0] == 401, "Missing caller key must return 401")
        passed("missing_caller_key_401")
        contract_status, contract_bytes = api(fixture, "/contracts/emenda-intake-v1.json", caller=None)
        require(contract_status == 200, "Product intake contract is not published")
        contract = json_body(contract_bytes)
        require("/api/intake/uploads" in contract["paths"], "Published contract missing product intake")
        passed("published_http_contract")
        student = secrets.token_hex(18)  # exactly 36 chars, the accepted boundary
        key = "acceptance-" + secrets.token_hex(8)
        images = [png((218, 232, 255)), png((255, 229, 211))]
        first = completed(upload_retry(fixture, student, key, images), 2, 2)
        baseline = count_items(fixture)
        require(baseline == 2, "First batch created unexpected database rows")
        passed("real_png_batch_upload_student_id_36", items=2, images=2)
        too_long = upload(fixture, student + "x", key + "-long", images)
        require(too_long[0] == 400 and too_long[1].get("success") is False, "studentId >36 must fail validation")
        passed("student_id_37_rejected")
        for item in first["items"]:
            signed = envelope(api(fixture, "/api/mistakes/presigned-url", "POST", {"objectPath": item["imagePaths"][0], "expirySeconds": 60, "size": "full"}))
            url = signed["url"]
            parsed = urllib.parse.urlsplit(url)
            require(parsed.scheme == "http" and parsed.netloc == fixture["s3_endpoint"], "Signed read escaped the isolated S3 endpoint")
            require("X-Amz-Signature" in urllib.parse.parse_qs(parsed.query), "Signed URL has no S3 signature")
            code, content = request(url)
            require(code == 200 and content in images, "Signed S3 read did not return the uploaded PNG bytes")
            unsigned = urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))
            require(request(unsigned)[0] == 403, "S3 bucket must not allow anonymous object reads")
        passed("private_real_s3_signed_reads", objects=2)
        require(identity(completed(upload_retry(fixture, student, key, images), 2, 2)) == identity(first), "Same-key replay changed identity")
        require(count_items(fixture) == baseline, "Replay created duplicate database items")
        passed("same_key_identical_replay")
        for changed_student, changed_images in ((student, [images[1], images[0]]), (student, [images[0], png((177, 221, 188))]), (secrets.token_hex(18), images)):
            conflict = upload(fixture, changed_student, key, changed_images)
            require(conflict[0] == 409 and conflict[1].get("success") is False and conflict[1].get("errorCode") == "source_conflict", "Changed content/student under the same key must return 409 source_conflict")
        changed_type, multipart_type = multipart(student, key, images)
        changed_type = changed_type.replace(b"Content-Type: image/png", b"Content-Type: image/jpeg")
        conflict_status, conflict_raw = api(fixture, "/api/intake/uploads", "POST", raw=changed_type, content_type=multipart_type)
        require(conflict_status == 409 and json_body(conflict_raw).get("errorCode") == "source_conflict", "Changed image media type under the same key must return 409 source_conflict")
        require(count_items(fixture) == baseline, "Conflicting replay changed database items")
        passed("same_key_different_student_or_content_409")
        second_caller = completed(upload_retry(fixture, student, key, images, caller=1), 2, 2)
        require(identity(second_caller) != identity(first), "Source key was not scoped to caller identity")
        require(count_items(fixture) == baseline + 2, "Second caller should own an independent intake")
        passed("source_key_scoped_to_trusted_backend_caller")
        parallel_key = key + "-parallel"
        before = count_items(fixture)
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
            futures = [pool.submit(upload_retry, fixture, student, parallel_key, images) for _ in range(6)]
            parallel = [completed(future.result(), 2, 2) for future in futures]
        require(all(identity(body) == identity(parallel[0]) for body in parallel), "Concurrent identical requests diverged")
        require(count_items(fixture) == before + 2, "Concurrent identical requests created duplicates")
        passed("six_concurrent_identical_requests", callers=6, uniqueItems=2)
        item = first["items"][0]
        reviewed = envelope(api(fixture, "/api/mistakes/" + item["id"] + "/review", "POST",
                               {"reviewStatus": 2, "reviewerId": "verification-reviewer", "rootCause": "验收确认", "grade": 7, "subject": 2}))
        require(reviewed.get("success") is True, "Review inner success must be true")
        practice = envelope(api(fixture, "/api/mistakes/" + item["id"] + "/practices", "POST",
                               {"studentId": student, "studentAnswer": "42", "isCorrect": True, "comments": "isolated acceptance", "imagePaths": []}))
        practices = envelope(api(fixture, "/api/mistakes/" + item["id"] + "/practices"))["practices"]
        require(any(row["id"] == practice["id"] and row["studentAnswer"] == "42" and row["isCorrect"] is True for row in practices), "Practice write/read did not round-trip")
        passed("review_and_practice_existing_http_contract")
        before = count_items(fixture)
        owned(fixture, fixture["containers"]["host"])
        docker(fixture, "restart", fixture["containers"]["host"])
        wait_until(lambda: api(fixture, "/health/ready", caller=None)[0] == 200, "Host restart readiness", seconds=120)
        require(identity(completed(upload_retry(fixture, student, key, images), 2, 2)) == identity(first), "Restart lost first intake identity")
        require(identity(completed(upload_retry(fixture, student, parallel_key, images), 2, 2)) == identity(parallel[0]), "Restart lost concurrent intake identity")
        require(count_items(fixture) == before, "Restart/replay duplicated persisted items")
        require(any(row["id"] == practice["id"] for row in envelope(api(fixture, "/api/mistakes/" + item["id"] + "/practices"))["practices"]), "Restart lost practice")
        passed("host_restart_persisted_identity_and_practice")
        recovery_key = key + "-s3-recovery"
        before = count_items(fixture)
        owned(fixture, fixture["containers"]["s3"])
        docker(fixture, "stop", fixture["containers"]["s3"])
        try:
            failed = upload(fixture, student, recovery_key, images)
            require(failed[0] >= 400 and failed[1].get("success") is False, "Storage outage was reported as success")
            require(count_items(fixture) == before, "Failed storage upload created mistake items")
        finally:
            docker(fixture, "start", fixture["containers"]["s3"])
        wait_until(lambda: request("http://" + fixture["s3_endpoint"])[0] == 403, "S3 recovery")
        recovered = completed(upload_retry(fixture, student, recovery_key, images), 2, 2)
        require(identity(completed(upload_retry(fixture, student, recovery_key, images), 2, 2)) == identity(recovered), "Recovered upload did not remain idempotent")
        require(count_items(fixture) == before + 2, "S3 recovery created duplicate items")
        passed("real_s3_outage_same_key_same_content_recovery")
        recovery_key = key + "-vl-recovery"
        before = count_items(fixture)
        control_vl(fixture, "fail")
        try:
            failed = upload(fixture, student, recovery_key, images)
            require(failed[0] >= 400 and failed[1].get("success") is False, "VL failure was reported as success")
            require(count_items(fixture) == before, "Failed analysis created mistake items")
        finally:
            control_vl(fixture, "ok")
        completed(upload_retry(fixture, student, recovery_key, images), 2, 2)
        require(count_items(fixture) == before + 2, "Analysis recovery did not create the complete batch exactly once")
        passed("analysis_failure_same_key_same_content_recovery")
        # Inject a deterministic second-group persistence failure only into this owned database.
        # A nontransactional sequence makes this work even when the Host correctly uses one transaction.
        before = count_items(fixture)
        logs_before = int(sql(fixture, "SELECT count(*) FROM process_log;"))
        sql(fixture, "CREATE SEQUENCE verification_insert_attempt; CREATE FUNCTION verification_fail_second() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF nextval('verification_insert_attempt')=2 THEN RAISE EXCEPTION 'isolated verification fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER verification_fail_second BEFORE INSERT ON mistake_item FOR EACH ROW EXECUTE FUNCTION verification_fail_second();")
        recovery_key = key + "-batch-recovery"
        try:
            failed = upload(fixture, student, recovery_key, images)
            require(failed[0] >= 400 and failed[1].get("success") is False, "Partial batch persistence was reported as success")
            require(count_items(fixture) == before, "Failed batch partially committed mistake items")
            require(int(sql(fixture, "SELECT count(*) FROM process_log;")) == logs_before, "Failed batch partially committed process logs")
        finally:
            sql(fixture, "DROP TRIGGER verification_fail_second ON mistake_item; DROP FUNCTION verification_fail_second(); DROP SEQUENCE verification_insert_attempt;")
        recovered = completed(upload_retry(fixture, student, recovery_key, images), 2, 2)
        require(count_items(fixture) == before + 2, "Partial batch recovery duplicated or omitted items")
        require(identity(completed(upload_retry(fixture, student, recovery_key, images), 2, 2)) == identity(recovered), "Partial batch recovery was not stable on replay")
        passed("partial_batch_failure_not_success_and_same_key_recovery", recoveredItems=2)
        results["success"] = True
        fixture["status"] = "verified"
    except Failure as error:
        results["failure"] = str(error)
        fixture["status"] = "failed"
        raise
    except Exception:
        results["failure"] = "Unexpected verification error (sensitive details suppressed)"
        fixture["status"] = "failed"
        raise Failure(results["failure"]) from None
    finally:
        # The report never includes response bodies, logs, credentials or bearer URLs.
        cleanup_errors = []
        actions = [lambda: control_vl(fixture, "ok")]
        if results["success"]:
            actions.append(lambda: remove_container(fixture, "host"))
        for action in actions:
            try:
                action()
            except Failure:
                cleanup_errors.append("Owned resource reset failed; run cleanup with this fixture")
        if cleanup_errors:
            results["cleanupWarnings"] = cleanup_errors
        results["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        private_json(results_path, results)
        private_json(path, fixture)
        print("Redacted result: " + str(results_path))
    return 0


def cleanup(args):
    path, fixture = load_fixture(args.fixture)
    for role in ("host", "vl", "s3", "pg"):
        remove_container(fixture, role)
        private_json(path, fixture)
    if "network" in fixture["created"]:
        owned(fixture, fixture["network"], "network")
        docker(fixture, "network", "rm", fixture["network"])
        fixture["created"].remove("network")
        private_json(path, fixture)
    if fixture["owned_profile"]:
        require(fixture["colima_profile"] == fixture["owner"], "Colima profile owner mismatch")
        command(["colima", "delete", fixture["colima_profile"], "--force"], timeout=180)
        fixture["owned_profile"] = False
    fixture["status"] = "cleaned"
    private_json(path, fixture)
    print("Owned resources cleaned; private fixture retained: " + str(path))


def serve_vl():
    """Explicit deterministic provider fixture; no model-quality claim is made."""
    state = {"fail": False}
    lock = threading.Lock()
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass
        def reply(self, status, value):
            content = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
        def read_body(self):
            if self.headers.get("Transfer-Encoding", "").lower() != "chunked":
                return self.rfile.read(int(self.headers.get("Content-Length", "0")))
            chunks = []
            while True:
                size = int(self.rfile.readline().split(b";", 1)[0].strip(), 16)
                if size == 0:
                    while self.rfile.readline().strip():
                        pass
                    return b"".join(chunks)
                chunk = self.rfile.read(size)
                if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                    raise ValueError("Invalid chunk framing")
                chunks.append(chunk)
        def do_GET(self):
            self.reply(200, {"fixture": "deterministic-vl"})
        def do_POST(self):
            if self.headers.get("Authorization") != "Bearer " + os.environ["VL_CONTROL_KEY"]:
                self.reply(401, {"error": "unauthorized"})
                return
            try:
                body = self.read_body()
            except ValueError:
                self.reply(400, {"error": "invalid provider fixture framing"})
                return
            if self.path == "/control":
                with lock:
                    state["fail"] = body == b"fail"
                self.reply(200, {"ok": True})
                return
            if self.path != "/v1/chat/completions":
                self.reply(404, {"error": "unknown fixture path"})
                return
            with lock:
                fail = state["fail"]
            if fail:
                self.reply(503, {"error": {"message": "isolated acceptance fault"}})
                return
            try:
                data = json.loads(body)
                count = sum(part.get("type") == "image_url" for message in data["messages"] for part in message["content"])
                require(count > 0, "VL fixture requires actual image parts")
                groups = [{"imageIndices": [index], "subject": 2, "grade": 7, "rootCause": "验收固定分析"} for index in range(count)]
                self.reply(200, {"choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": json.dumps(groups, ensure_ascii=False)}}]})
            except (KeyError, ValueError, TypeError, Failure):
                self.reply(400, {"error": "invalid provider fixture request"})
    http.server.ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    p = sub.add_parser("prepare", help="Prepare a new owned PG15 + private real S3 fixture; no Host/build/test")
    p.add_argument("--fixture", required=True)
    transport = p.add_mutually_exclusive_group()
    transport.add_argument("--docker-host", help="Explicit Docker daemon endpoint (no global context mutation)")
    transport.add_argument("--colima-profile", help="Reuse a separately created task-owned profile; never started/stopped/deleted here")
    p = sub.add_parser("run", help="Run acceptance against a prebuilt actual dotnet Host")
    p.add_argument("--fixture", required=True)
    p.add_argument("--host-dir", required=True)
    p.add_argument("--results")
    p = sub.add_parser("cleanup", help="Remove only resources recorded and owner-labelled by this fixture")
    p.add_argument("--fixture", required=True)
    sub.add_parser("serve-vl", help=argparse.SUPPRESS)
    args = parser.parse_args()
    try:
        if args.action == "serve-vl":
            serve_vl()
        elif args.action == "prepare":
            prepare(args)
        elif args.action == "cleanup":
            cleanup(args)
        else:
            return run(args)
    except KeyboardInterrupt:
        print("Interrupted; use the private fixture to clean up owned resources", file=sys.stderr)
        return 130
    except Failure as error:
        print("FAIL " + str(error), file=sys.stderr)
        return 1
    except Exception:
        # Exception messages may embed credentials, connection strings or signed URLs.
        print("FAIL Unexpected verification error (details suppressed); inspect the redacted result and owned resources privately", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
