FROM python:3.12-alpine
RUN pip install --no-cache-dir "PyJWT[crypto]==2.9.0"
RUN addgroup -S app && adduser -S -G app app
COPY app.py /app.py
USER app
EXPOSE 8080
CMD ["python", "/app.py"]
