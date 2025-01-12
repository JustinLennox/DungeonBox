import json
import boto3
import uuid
import string
import random

bedrock_agent_runtime = boto3.client('bedrock-agent-runtime')

def generate_session_id(length=32):
    """Generate a random session ID"""
    characters = string.ascii_letters + string.digits
    return ''.join(random.choice(characters) for _ in range(length))

def lambda_handler(event, context):
    try:
        # Get the user message from the event
        user_message = event.get('message', '')
        
        session_id = event.get('sessionId', '')
        if len(session_id) == 0:
            session_id = generate_session_id()
        
        # Parameters for the agent
        agent_id = "2CK7NVNQIM"
        agent_alias_id = "PMPFU1UDWK"
        
        # Send message to the agent
        response = bedrock_agent_runtime.invoke_agent(
            agentId=agent_id,
            agentAliasId=agent_alias_id,
            sessionId=session_id,
            inputText=user_message
        )
        
        # Process the streaming response
        full_response = ""
        for event in response['completion']:
            if 'chunk' in event:
                chunk = event['chunk']
                if 'bytes' in chunk:
                    # Decode the bytes to text
                    text = chunk['bytes'].decode('utf-8')
                    full_response += text
        
        return {
            'statusCode': 200,
            'body': json.dumps({
                'response': full_response,
                'sessionId': session_id
            })
        }
        
    except Exception as e:
        return {
            'statusCode': 500,
            'body': json.dumps({
                'error': str(e)
            })
        }

