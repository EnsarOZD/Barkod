const { createApp, ref, computed } = Vue;

// UUID generator for idempotency key
function uuidv4() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
        var r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
        return v.toString(16);
    });
}

createApp({
    setup() {
        const pin = ref(localStorage.getItem('print_pin') || '');
        const isLoading = ref(false);
        const message = ref('');
        const messageType = ref('');

        const form = ref({
            projectName: '',
            location: '',
            projectCode: null,
            boxCount: null
        });

        const isFormValid = computed(() => {
            return pin.value.length > 0 &&
                   form.value.projectName.trim().length > 0 &&
                   form.value.location.trim().length > 0 &&
                   form.value.projectCode > 0 &&
                   form.value.boxCount > 0 &&
                   form.value.boxCount <= 50;
        });

        const submitPrintJob = async () => {
            if (!isFormValid.value) return;

            // Save PIN to local storage for convenience
            localStorage.setItem('print_pin', pin.value);

            isLoading.value = true;
            message.value = '';
            
            try {
                // Generate a fresh Idempotency Key for this attempt
                const idempotencyKey = uuidv4();

                const response = await fetch('/api/print', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-Print-Pin': pin.value
                    },
                    body: JSON.stringify({
                        idempotencyKey: idempotencyKey,
                        projectName: form.value.projectName.trim(),
                        location: form.value.location.trim(),
                        projectCode: form.value.projectCode,
                        boxCount: form.value.boxCount
                    })
                });

                const data = await response.json();

                if (response.ok) {
                    messageType.value = 'success';
                    message.value = data.message;
                    
                    // Reset only numerical inputs to encourage printing next batch easily
                    // form.value.projectCode = null;
                    // form.value.boxCount = null;
                } else {
                    messageType.value = 'error';
                    message.value = data.message || 'Bir hata oluştu.';
                }
            } catch (error) {
                messageType.value = 'error';
                message.value = 'Sunucuya ulaşılamadı. ' + error.message;
            } finally {
                isLoading.value = false;
                
                // Clear success message after 5 seconds
                if(messageType.value === 'success') {
                    setTimeout(() => {
                        message.value = '';
                    }, 5000);
                }
            }
        };

        return {
            pin,
            form,
            isLoading,
            isFormValid,
            message,
            messageType,
            submitPrintJob
        }
    }
}).mount('#app');
