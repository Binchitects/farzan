# The security scanners (compose profile "scanners"): opengrep's engine with Arena's own rules (rules/, tested here on
# their own examples), Bandit for Python, gitleaks for secrets in the code and its history, and Trivy's checks of
# configuration (Dockerfiles, Kubernetes, Helm, Terraform). Built once; at run time it reaches nothing, so each one's
# rules and checks are the ones built in. Each download is pinned and its SHA-256 checked.
#
# Not here: gosec and the .NET analyzers, which build the project and so fetch its packages; Arena's Go and C# rules
# stand in for them.
# gitleaks from its source at its release's commit, built with the newest Go and the libraries it reads raised to
# their fixed versions: the release's own binary (8.30.1) was built with an older Go and older x/crypto, x/text and
# compress, each with known vulnerabilities. Checked: the commit is the tag's, and the binary finds a planted token.
FROM golang:1.27-bookworm AS gitleaks
ARG GITLEAKS_VERSION=8.30.1
ARG GITLEAKS_COMMIT=83d9cd684c87d95d656c1458ef04895a7f1cbd8e
# proxy.golang.org and sum.golang.org answer 403 from some networks: the modules through goproxy.cn, each checked
# against Go's checksum database through its official mirror (sum.golang.google.cn).
ENV GOTOOLCHAIN=local CGO_ENABLED=0 GOFLAGS=-trimpath GOPROXY=https://goproxy.cn,direct GOSUMDB=sum.golang.google.cn
RUN git clone --quiet --branch "v${GITLEAKS_VERSION}" https://github.com/gitleaks/gitleaks /src \
 && cd /src && test "$(git rev-parse HEAD)" = "${GITLEAKS_COMMIT}" \
 && go get golang.org/x/crypto@v0.55.0 golang.org/x/text@v0.41.0 github.com/klauspost/compress@v1.18.7 \
 && go mod tidy \
 && go build -ldflags "-s -w -X github.com/zricethezav/gitleaks/v8/version.Version=v${GITLEAKS_VERSION}" -o /gitleaks . \
 && /gitleaks version \
 && mkdir /probe && cd /probe && git init -q && printf 'token = "ghp_%s"\n' "$(printf 'a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8')" > leak.txt \
 && git -c user.email=p@p -c user.name=p add leak.txt && git -c user.email=p@p -c user.name=p commit -qm probe \
 && if /gitleaks git --no-banner --exit-code 9 . >/dev/null 2>&1; then echo "gitleaks missed the probe"; exit 1; else test $? -eq 9; fi \
 && rm -rf /probe

FROM python:3.13-slim-bookworm
# PyPI over a slow or lossy link: a minute per read and ten tries before a package counts as missing (a read
# that timed out read as "no matching distribution"). Only pip reads it.
ENV PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10

ARG OPENGREP_VERSION=1.33.1
ARG OPENGREP_SHA256=1a78593c369cd58a8545a1dfb2ce548139f028a1af4ea7d08bc5ce8385cde243
ARG GITLEAKS_VERSION=8.30.1
ARG TRIVY_VERSION=0.76.2
ARG TRIVY_SHA256=bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf

# git, to check out a bundle's commit and for gitleaks to read its history (its C commands only: Perl, which only
# git's interactive and mail commands use, is removed again); opengrep's engine alone (LGPL-2.1: its command-line
# wrapper unpacks some 250 MB of Python at each start, and the scanner gives the engine its rules and files itself);
# and Trivy (Apache-2.0); gitleaks (MIT) comes built from the stage above. curl only while building. One layer, so what is removed takes no room.
RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends git ca-certificates curl \
 && curl -fsSL -o /tmp/opengrep.tgz "https://github.com/opengrep/opengrep/releases/download/v${OPENGREP_VERSION}/opengrep-core_linux_x86.tar.gz" \
 && echo "${OPENGREP_SHA256}  /tmp/opengrep.tgz" | sha256sum -c - \
 && mkdir -p /opt/opengrep && tar -xzf /tmp/opengrep.tgz -C /opt/opengrep opengrep-core \
 && curl -fsSL -o /tmp/trivy.tgz "https://github.com/aquasecurity/trivy/releases/download/v${TRIVY_VERSION}/trivy_${TRIVY_VERSION}_Linux-64bit.tar.gz" \
 && echo "${TRIVY_SHA256}  /tmp/trivy.tgz" | sha256sum -c - \
 && tar -xzf /tmp/trivy.tgz -C /usr/local/bin trivy \
 && rm /tmp/*.tgz \
 && apt-get purge -y curl && apt-get autoremove -y \
 && dpkg --remove --force-depends perl perl-modules-5.36 libperl5.36 liberror-perl \
 && rm -rf /var/lib/apt/lists/* /usr/share/doc/* \
 && /opt/opengrep/opengrep-core -version && trivy --version \
 && git init -q /tmp/g && git -C /tmp/g status --short && rm -rf /tmp/g

COPY --from=gitleaks /gitleaks /usr/local/bin/gitleaks
RUN gitleaks version

# Bandit (Apache-2.0), and PyYAML for the rules.
RUN pip install --no-cache-dir --only-binary=:all: bandit==1.9.4 pyyaml==6.0.3 \
 && bandit --version

COPY rules /opt/rules
COPY scan.py /app/scan.py
# The rules as the engine reads them, then the engine on their examples: the image is not built if one is wrong.
RUN SCANNERS_RULES=/opt/rules python -I /app/scan.py --build-rules \
 && SCANNERS_RULES=/opt/rules python -I /app/scan.py --self-test \
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} +

ENV SCANNERS_PORT=7800 \
    SCANNERS_WORK=/work \
    HOME=/tmp \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

# pip out of the image: nothing here installs at run time, and pip carries its own copies of urllib3, msgpack and
# setuptools (with known vulnerabilities in the ones it bundles); Python's ensurepip wheel with it.
RUN python3 -m pip uninstall -y pip \
 && rm -rf /usr/local/lib/python3.13/ensurepip/_bundled /usr/local/bin/pip /usr/local/bin/pip3 /usr/local/bin/pip3.13

USER 65534:65534
EXPOSE 7800
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s \
  CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:7800/health', timeout=8)"]
CMD ["python", "-I", "/app/scan.py"]
