pipeline {
    agent any

    environment {
        IMAGE_NAME     = 'deepakrajv/tmsbackend'
        CONTAINER_NAME = 'tmsbackend'
        GITHUB_REPO    = 'https://github.com/DeepakVijayasarathi/tmsbackend.git'

        DB_HOST        = 'postgres'
        DB_PORT        = '5432'
        DB_NAME        = 'tms'
        DB_USER        = 'postgres'

        DOCKER_NETWORK = 'tms-network'
        HEALTH_IMAGE   = 'curlimages/curl:8.12.1'
        PUBLIC_WEB_URL = 'https://tms.meettomanage.cloud'
        HOST_PORT      = '3300'
    }

    triggers {
        githubPush()
    }

    stages {
        stage('Checkout') {
            steps {
                git(
                    branch: 'main',
                    url: "${GITHUB_REPO}",
                    credentialsId: 'github'
                )
            }
        }

        stage('Build Docker Image') {
            steps {
                sh '''
                    docker build \
                      --tag "${IMAGE_NAME}:latest" \
                      --file Dockerfile .
                '''
            }
        }

        stage('Create Docker Network') {
            steps {
                sh '''
                    docker network inspect "${DOCKER_NETWORK}" >/dev/null 2>&1 || \
                    docker network create "${DOCKER_NETWORK}"

                    if docker container inspect postgres >/dev/null 2>&1; then
                        docker network connect "${DOCKER_NETWORK}" postgres >/dev/null 2>&1 || true
                    fi
                '''
            }
        }

        stage('Deploy Container') {
            steps {
                withCredentials([
                    string(credentialsId: 'tms-db-password', variable: 'DB_PASSWORD'),
                    string(credentialsId: 'tms-jwt-key', variable: 'JWT_KEY')
                ]) {
                    sh '''
                        docker rm --force "${CONTAINER_NAME}" >/dev/null 2>&1 || true

                        docker run --detach \
                          --name "${CONTAINER_NAME}" \
                          --network "${DOCKER_NETWORK}" \
                          --publish "${HOST_PORT}:8080" \
                          --restart unless-stopped \
                          --env ASPNETCORE_ENVIRONMENT=Production \
                          --env ASPNETCORE_URLS=http://+:8080 \
                          --env Database__Provider=Postgres \
                          --env "Database__ConnectionString=Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}" \
                          --env "Auth__JwtKey=${JWT_KEY}" \
                          --env "App__PublicWebUrl=${PUBLIC_WEB_URL}" \
                          --env App__SeedDemoData=false \
                          --env Payments__Sandbox__Enabled=false \
                          --env Jitsi__Domain=meet.techmisai.com \
                          "${IMAGE_NAME}:latest"
                    '''
                }
            }
        }

        stage('Verify Deployment') {
            steps {
                sh '''
                    set -eu

                    attempt=1
                    until docker run --rm \
                      --network "${DOCKER_NETWORK}" \
                      "${HEALTH_IMAGE}" \
                      --fail --silent --show-error \
                      "http://${CONTAINER_NAME}:8080/api/v1/health" >/dev/null; do
                        if [ "$attempt" -ge 12 ]; then
                            echo "ERROR: Backend health check failed after ${attempt} attempts."
                            docker ps --all --filter "name=^/${CONTAINER_NAME}$"
                            docker logs --tail 100 "${CONTAINER_NAME}" || true
                            exit 1
                        fi

                        echo "Waiting for backend health check... attempt ${attempt}/12"
                        sleep 5
                        attempt=$((attempt + 1))
                    done

                    echo "Health check passed."
                    docker ps --filter "name=^/${CONTAINER_NAME}$" --filter status=running
                    docker logs --tail 50 "${CONTAINER_NAME}"
                '''
            }
        }
    }

    post {
        success {
            echo 'TMS backend deployed successfully on port 3300.'
        }

        failure {
            sh 'docker logs --tail 100 "${CONTAINER_NAME}" || true'
            echo 'TMS backend deployment failed. Check the Jenkins console logs.'
        }

        always {
            cleanWs()
        }
    }
}
