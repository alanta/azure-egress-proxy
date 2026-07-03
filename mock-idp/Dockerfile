FROM python:3.12-alpine
RUN pip install --no-cache-dir "PyJWT[crypto]==2.9.0"
COPY app.py /app.py
EXPOSE 8080
CMD ["python", "/app.py"]
